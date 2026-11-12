using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Genies.Sdk;
using Genies.Sdk.Samples.AvatarStarter;
using Newtonsoft.Json.Linq;
using UnityEngine;
using UnityEngine.Networking;

/// <summary>
/// Recovery tool for test runs whose ImgBB screenshot uploads failed.
///
/// Scans every bin in the JSONBin collection, finds runs where any of
/// final_image_portrait / final_image_front / final_image_side is missing,
/// loads a fresh avatar from the saved avatar_definition, verifies the loaded
/// avatar actually matches that definition, re-captures the shots with the same
/// CharacterPhotographer used during the study, uploads them to ImgBB and patches
/// ONLY the missing image fields back into the bin.
///
/// Usage: add this component to SampleScene, wire the references, enter Play mode
/// and log in to Genies, but do NOT start a session (the avatar editor must be
/// closed). Then use the component's context menu "Run Recovery" (or tick Run On Start).
/// Leave "Dry Run" on for a first pass: it captures and saves locally but uploads
/// and modifies nothing.
/// </summary>
public class MissingImageRecovery : MonoBehaviour
{
    [Header("Scene References")]
    [SerializeField] private LoadMyAvatar _loadMyAvatar;
    [SerializeField] private CharacterPhotographer _characterPhotographer;
    [SerializeField] private ImgBBManager _imgBBManager;

    [Header("Behaviour")]
    [Tooltip("Start automatically once the avatar is loaded.")]
    [SerializeField] private bool _runOnStart = false;

    [Tooltip("Capture and save screenshots locally, but do NOT upload to ImgBB or modify JSONBin.")]
    [SerializeField] private bool _dryRun = true;

    [Tooltip("Only process runs whose 'platform' field is in this list. Note: mobile runs were originally photographed in the mobile app's scene, so their re-renders here use the VR scene's background.")]
    [SerializeField] private List<string> _platforms = new List<string> { "vr", "mobile" };

    [Tooltip("Also re-capture runs that already have all images (useful with Dry Run to compare a recovered shot against an original). Existing URLs are never overwritten.")]
    [SerializeField] private bool _includeCompleteRuns = false;

    [Tooltip("If non-empty, only process bins whose name (participant id) is in this list.")]
    [SerializeField] private List<string> _onlyParticipants = new List<string>();

    [Tooltip("Bins whose name starts with any of these are skipped (e.g. the default avatar bins).")]
    [SerializeField] private List<string> _skipBinPrefixes = new List<string> { "default-" };

    [Header("Timing")]
    [Tooltip("Seconds to wait after the avatar is built so textures and the idle animation settle before capture.")]
    [SerializeField] private float _settleSeconds = 3f;
    [Tooltip("Minimum seconds between JSONBin requests (JSONBin returns 429 when requests come too fast).")]
    [SerializeField] private float _jsonBinMinInterval = 1.0f;
    [SerializeField] private int _jsonBinMaxAttempts = 6;
    [SerializeField] private int _uploadAttempts = 5;

    [Header("Output")]
    [Tooltip("Local folder (relative to the project root) where recovered screenshots and the report are saved.")]
    [SerializeField] private string _outputFolder = "RecoveredShots";

    private const int BINS_PER_PAGE = 10;
    // CharacterPhotographer returns paths in this order: [0]=Portrait, [1]=Front, [2]=Side
    private static readonly string[] ImageFields = { "final_image_portrait", "final_image_front", "final_image_side" };

    private bool _running;
    private float _lastJsonBinRequestTime = -999f;

    private class BinRef
    {
        public string Id;
        public string Name;
    }

    private void Start()
    {
        if (_runOnStart) RunRecovery();
    }

    [ContextMenu("Run Recovery")]
    public async void RunRecovery()
    {
        if (!Application.isPlaying)
        {
            Debug.LogError("[Recovery] Enter Play mode (and log in) before running.");
            return;
        }
        if (_running)
        {
            Debug.LogWarning("[Recovery] Already running.");
            return;
        }

        _running = true;
        try
        {
            await RunRecoveryAsync();
        }
        catch (Exception e)
        {
            Debug.LogError($"[Recovery] Aborted: {e}");
        }
        finally
        {
            _running = false;
        }
    }

    private async Task RunRecoveryAsync()
    {
        string outRoot = Path.GetFullPath(Path.Combine(Application.dataPath, "..", _outputFolder));
        Directory.CreateDirectory(outRoot);
        var report = new StringBuilder("bin_id,participant_id,platform,status,final_image_portrait,final_image_front,final_image_side,note\n");
        var counts = new SortedDictionary<string, int>();

        void Record(BinRef bin, string platform, string status, JObject run, string note)
        {
            counts[status] = counts.TryGetValue(status, out int c) ? c + 1 : 1;
            report.AppendLine(Csv(bin.Id, bin.Name, platform, status,
                run == null ? "" : Url(run, 0), run == null ? "" : Url(run, 1), run == null ? "" : Url(run, 2), note));
        }

        Log($"Starting. DryRun={_dryRun}. Platforms=[{string.Join(",", _platforms)}]. Output: {outRoot}");

        // 1. Wait for the scene avatar (requires Genies login). It is used as the position/orientation reference.
        Log("Waiting for avatar to load (log in if prompted)...");
        while (_loadMyAvatar == null || !_loadMyAvatar.IsAvatarLoaded)
            await Task.Delay(250);
        await Task.Delay(500); // let MainManager hide the model first

        if (AvatarSdk.IsAvatarEditorOpen)
        {
            LogErr("The avatar editor is open (a session was started). Restart Play mode, log in, and run recovery " +
                   "WITHOUT starting a session; the open editor interferes with the avatar and appears in the shots.");
            return;
        }

        ManagedAvatar sceneAvatar = _loadMyAvatar.LoadedAvatar;

        // 2. List every bin in the collection.
        List<BinRef> bins = await ListAllBinsAsync();
        Log($"Found {bins.Count} bins in collection.");

        try
        {
            // The scene avatar stays in place (so CharacterPhotographer finds the same orientation
            // source as during the study) but is hidden; each run gets its own freshly built avatar.
            sceneAvatar.ModelRoot.SetActive(false);

            for (int b = 0; b < bins.Count; b++)
            {
                BinRef bin = bins[b];
                string label = $"[{b + 1}/{bins.Count}] '{bin.Name}' ({bin.Id})";

                if (_skipBinPrefixes.Any(p => !string.IsNullOrEmpty(p) && (bin.Name ?? "").StartsWith(p)))
                {
                    Record(bin, "", "skipped_prefix", null, "");
                    continue;
                }
                if (_onlyParticipants.Count > 0 && !_onlyParticipants.Contains(bin.Name))
                    continue;

                // 3. Download and inspect.
                string raw = await GetBinAsync(bin.Id);
                if (raw == null)
                {
                    Record(bin, "", "download_failed", null, "");
                    continue;
                }

                JObject run;
                try { run = ParseExact(raw); }
                catch (Exception e)
                {
                    LogErr($"{label}: could not parse JSON: {e.Message}");
                    Record(bin, "", "parse_failed", null, e.Message);
                    continue;
                }

                string platform = (string)run["platform"] ?? "";
                bool[] missing = ImageFields.Select(f => IsEmpty(run[f])).ToArray();
                bool anyMissing = missing.Any(m => m);

                if (!anyMissing && !_includeCompleteRuns)
                {
                    Record(bin, platform, "complete", run, "");
                    continue;
                }
                if (_platforms.Count > 0 && !_platforms.Contains(platform))
                {
                    Record(bin, platform, anyMissing ? "missing_other_platform" : "complete", run,
                        anyMissing ? "not re-rendered: different platform/scene" : "");
                    continue;
                }

                if (anyMissing)
                    Log($"{label}: missing {string.Join(", ", ImageFields.Where((f, i) => missing[i]))}");

                string definition = (string)run["avatar_definition"];
                if (string.IsNullOrWhiteSpace(definition))
                {
                    LogErr($"{label}: no avatar_definition saved, cannot regenerate.");
                    Record(bin, platform, "no_definition", run, "");
                    continue;
                }

                // 4. Build a fresh avatar from the definition, verify it, capture.
                string runDir = Path.Combine(outRoot, Sanitize($"{bin.Name}_{bin.Id}"));
                CaptureResult cap = await CaptureForDefinitionAsync(sceneAvatar, definition, runDir, label);

                if (cap.Files == null)
                {
                    Record(bin, platform, "capture_failed", run, cap.Problem);
                    continue;
                }
                if (cap.Problem != null)
                {
                    // Shots are saved for inspection but never uploaded: they do not show the participant's avatar.
                    LogErr($"{label}: loaded avatar does not match the saved definition ({cap.Problem}). Not uploading.");
                    Record(bin, platform, "definition_mismatch", run, $"{cap.Problem} | {runDir}");
                    continue;
                }

                if (_dryRun || !anyMissing)
                {
                    Record(bin, platform, anyMissing ? "dry_run_captured" : "complete_recaptured_locally", run, runDir);
                    continue;
                }

                // 5. Re-check right before uploading: only fields that are STILL empty get an image.
                string freshRaw = await GetBinAsync(bin.Id);
                if (freshRaw == null)
                {
                    Record(bin, platform, "recheck_failed", run, $"not uploaded | {runDir}");
                    continue;
                }
                JObject fresh = ParseExact(freshRaw);
                bool[] stillMissing = ImageFields.Select(f => IsEmpty(fresh[f])).ToArray();
                if (!stillMissing.Any(m => m))
                {
                    Log($"{label}: images were filled in since the first read; nothing to do.");
                    Record(bin, platform, "already_filled", fresh, runDir);
                    continue;
                }

                // 6. Upload only the missing images.
                var newUrls = new string[3];
                bool allUploaded = true;
                for (int i = 0; i < 3; i++)
                {
                    if (!stillMissing[i]) continue;
                    string url = await UploadWithRetryAsync(cap.Files[i]);
                    if (url != null && url.StartsWith("https://"))
                        newUrls[i] = url;
                    else
                    {
                        allUploaded = false;
                        LogErr($"{label}: upload of {ImageFields[i]} failed after {_uploadAttempts} attempts.");
                    }
                }

                if (newUrls.All(u => u == null))
                {
                    Record(bin, platform, "upload_failed", fresh, runDir);
                    continue;
                }

                // 7. Patch ONLY the still-empty image fields on the freshly read bin, then verify
                //    nothing else changed before writing (JSONBin also keeps the previous version).
                JObject patched = (JObject)fresh.DeepClone();
                for (int i = 0; i < 3; i++)
                    if (stillMissing[i] && newUrls[i] != null)
                        patched[ImageFields[i]] = newUrls[i];
                patched["images_recovered"] = true;
                patched["images_recovered_ts"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

                string safety = CheckOnlyImageFieldsChanged(fresh, patched);
                if (safety != null)
                {
                    LogErr($"{label}: refusing to write, {safety}");
                    Record(bin, platform, "safety_check_failed", fresh, $"{safety} | uploaded: {string.Join(" ", newUrls.Where(u => u != null))}");
                    continue;
                }

                bool updated = await UpdateBinAsync(bin.Id, patched.ToString(Newtonsoft.Json.Formatting.None));

                string status = updated && allUploaded ? "fixed" : updated ? "partially_fixed" : "update_failed";
                string note = updated ? runDir : $"bin NOT updated; uploaded URLs: {string.Join(" ", newUrls.Where(u => u != null))} | {runDir}";
                Record(bin, platform, status, updated ? patched : fresh, note);
                Log($"{label}: {status}");
            }
        }
        finally
        {
            if (sceneAvatar != null && !sceneAvatar.IsDisposed && sceneAvatar.ModelRoot != null)
                sceneAvatar.ModelRoot.SetActive(true);

            string reportPath = Path.Combine(outRoot, $"recovery_report_{DateTime.Now:yyyyMMdd_HHmmss}.csv");
            File.WriteAllText(reportPath, report.ToString());
            Log($"<color=green>Done.</color> DryRun={_dryRun}. " +
                string.Join(", ", counts.Select(kv => $"{kv.Key}: {kv.Value}")) + $". Report: {reportPath}");
        }
    }

    // ---------------- Avatar + capture ---------------- //

    private struct CaptureResult
    {
        public string[] Files;   // local copies [Portrait, Front, Side], null if capture failed
        public string Problem;   // null if the avatar matched its definition
    }

    private async Task<CaptureResult> CaptureForDefinitionAsync(ManagedAvatar sceneAvatar, string definition, string runDir, string label)
    {
        ManagedAvatar avatar = null;
        try
        {
            Transform sceneRoot = sceneAvatar.Root.transform;

            // Older definitions (e.g. from the mobile app) can contain legacy "Static/..." asset ids that the
            // current SDK does not resolve on the direct load path. Convert them with the SDK's own mapping.
            // This only affects what we render; the stored definition is never modified.
            string original = definition;
            definition = MigrateLegacyIds(definition, out List<string> migrated);
            if (migrated.Count > 0)
                Log($"{label}: converted legacy ids: {string.Join(", ", migrated)}");

            await AvatarSdk.PrecacheAvatarAssetsByDefinitionAsync(definition);
            avatar = await AvatarSdk.LoadAvatarByDefinitionAsync(
                definition,
                avatarName: "RecoveryAvatar",
                parent: sceneRoot.parent,
                playerAnimationController: _loadMyAvatar.OptionalController);

            if (avatar == null || avatar.Root == null)
                return new CaptureResult { Problem = "LoadAvatarByDefinitionAsync returned null" };

            // Same placement as the study avatar.
            avatar.Root.transform.localPosition = sceneRoot.localPosition;
            avatar.Root.transform.localRotation = sceneRoot.localRotation;
            avatar.Root.transform.localScale = sceneRoot.localScale;
            avatar.ModelRoot.SetActive(true);

            string problem = CompareDefinitions(definition, avatar.GetDefinition());
            if (problem != null)
            {
                // One retry: re-apply onto the fresh avatar in case an asset load raced.
                LogWarn($"{label}: mismatch after load ({problem}); re-applying definition once.");
                await avatar.SetDefinitionAsync(definition);
                problem = CompareDefinitions(definition, avatar.GetDefinition());
            }

            await Task.Delay(TimeSpan.FromSeconds(_settleSeconds));

            List<string> captured = await _characterPhotographer.CaptureAllShotsAsync();
            if (captured == null || captured.Count < 3)
                return new CaptureResult { Problem = "CharacterPhotographer returned no images" };

            // Keep a permanent local copy (the photographer overwrites the same 3 files every time).
            Directory.CreateDirectory(runDir);
            var files = new string[3];
            for (int i = 0; i < 3; i++)
            {
                files[i] = Path.Combine(runDir, Path.GetFileName(captured[i]));
                File.Copy(captured[i], files[i], true);
            }
            File.WriteAllText(Path.Combine(runDir, "avatar_definition.json"), original);
            if (migrated.Count > 0)
                File.WriteAllText(Path.Combine(runDir, "avatar_definition_rendered.json"), definition);
            Log($"{label}: saved shots to {runDir}");

            return new CaptureResult { Files = files, Problem = problem };
        }
        catch (Exception e)
        {
            LogErr($"{label}: capture failed: {e}");
            return new CaptureResult { Problem = e.Message };
        }
        finally
        {
            if (avatar != null && !avatar.IsDisposed)
            {
                GameObject root = avatar.Root;
                avatar.Dispose();
                if (root != null) Destroy(root);
            }
        }
    }

    private static IReadOnlyDictionary<string, string> _legacyIdMap;

    /// <summary>The SDK's legacy-id table (Genies.Naf.Content.StaticAssetMapping.Dictionary), read via reflection since the class is internal.</summary>
    private static IReadOnlyDictionary<string, string> LegacyIdMap
    {
        get
        {
            if (_legacyIdMap != null) return _legacyIdMap;
            Type type = AppDomain.CurrentDomain.GetAssemblies()
                .Select(a => a.GetType("Genies.Naf.Content.StaticAssetMapping", false))
                .FirstOrDefault(t => t != null);
            var field = type?.GetField("Dictionary",
                System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
            _legacyIdMap = field?.GetValue(null) as IReadOnlyDictionary<string, string>;
            if (_legacyIdMap == null)
            {
                LogWarn("Could not find the SDK's StaticAssetMapping; legacy ids will not be converted.");
                _legacyIdMap = new Dictionary<string, string>();
            }
            return _legacyIdMap;
        }
    }

    /// <summary>Replaces legacy asset/tattoo ids with their current equivalents. Returns the definition unchanged if none apply.</summary>
    private static string MigrateLegacyIds(string definition, out List<string> migrated)
    {
        migrated = new List<string>();
        JObject def = ParseExact(definition);
        var map = LegacyIdMap;

        if (def["equippedAssetIds"] is JArray assets)
        {
            var newIds = new List<string>();
            foreach (string id in assets.Values<string>())
            {
                if (id != null && map.TryGetValue(id, out string newId))
                {
                    migrated.Add($"{id} -> {newId}");
                    newIds.Add(newId);
                }
                else newIds.Add(id);
            }
            def["equippedAssetIds"] = new JArray(newIds.Distinct());
        }

        if (def["equippedTattooIds"] is JObject tattoos)
        {
            foreach (JProperty p in tattoos.Properties().ToList())
            {
                string id = (string)p.Value;
                if (id != null && map.TryGetValue(id, out string newId))
                {
                    migrated.Add($"{id} -> {newId}");
                    p.Value = newId;
                }
            }
        }

        return migrated.Count == 0 ? definition : def.ToString(Newtonsoft.Json.Formatting.None);
    }

    /// <summary>
    /// Compares equipped assets and tattoos of the wanted definition against what the avatar reports.
    /// Returns null when they match, otherwise a short description of the difference.
    /// </summary>
    private static string CompareDefinitions(string wanted, string actual)
    {
        if (string.IsNullOrWhiteSpace(actual)) return "avatar reported an empty definition";
        try
        {
            JObject w = JObject.Parse(wanted), a = JObject.Parse(actual);

            var wAssets = new HashSet<string>(w["equippedAssetIds"]?.Values<string>() ?? Enumerable.Empty<string>());
            var aAssets = new HashSet<string>(a["equippedAssetIds"]?.Values<string>() ?? Enumerable.Empty<string>());
            var notLoaded = wAssets.Except(aAssets).ToList();
            var extra = aAssets.Except(wAssets).ToList();

            var wTattoos = (w["equippedTattooIds"] as JObject)?.Properties().Select(p => $"{p.Name}={p.Value}").ToList() ?? new List<string>();
            var aTattoos = new HashSet<string>((a["equippedTattooIds"] as JObject)?.Properties().Select(p => $"{p.Name}={p.Value}") ?? Enumerable.Empty<string>());
            var tattoosMissing = wTattoos.Where(t => !aTattoos.Contains(t)).ToList();

            if (notLoaded.Count == 0 && extra.Count == 0 && tattoosMissing.Count == 0) return null;

            var parts = new List<string>();
            if (notLoaded.Count > 0) parts.Add($"not loaded: {string.Join(" ", notLoaded)}");
            if (extra.Count > 0) parts.Add($"unexpected: {string.Join(" ", extra)}");
            if (tattoosMissing.Count > 0) parts.Add($"tattoos missing: {string.Join(" ", tattoosMissing)}");
            return string.Join("; ", parts);
        }
        catch (Exception e)
        {
            return $"could not compare definitions: {e.Message}";
        }
    }

    private async Task<string> UploadWithRetryAsync(string filePath)
    {
        for (int attempt = 1; attempt <= _uploadAttempts; attempt++)
        {
            string url = await _imgBBManager.UploadImageAsync(filePath);
            if (!string.IsNullOrEmpty(url)) return url;

            int backoffMs = 2000 * attempt;
            LogWarn($"Upload attempt {attempt}/{_uploadAttempts} failed for {Path.GetFileName(filePath)}; retrying in {backoffMs / 1000}s.");
            await Task.Delay(backoffMs);
        }
        return null;
    }

    // ---------------- JSONBin ---------------- //

    private async Task<List<BinRef>> ListAllBinsAsync()
    {
        var result = new List<BinRef>();
        var seen = new HashSet<string>();
        string lastId = null;

        while (true)
        {
            string url = lastId == null
                ? $"{JSONBinManager.BASE_URL}/c/{JSONBinManager.COLLECTION_ID}/bins"
                : $"{JSONBinManager.BASE_URL}/c/{JSONBinManager.COLLECTION_ID}/bins/{lastId}";

            string body = await SendJsonBinAsync(() => UnityWebRequest.Get(url));
            if (body == null)
                throw new Exception("Failed to list collection bins; aborting so no runs are silently skipped.");

            JArray page = string.IsNullOrWhiteSpace(body) ? new JArray() : JArray.Parse(body);
            if (page.Count == 0) break;

            int added = 0;
            foreach (var item in page)
            {
                string id = (string)item["record"];
                if (string.IsNullOrEmpty(id) || !seen.Add(id)) continue;
                result.Add(new BinRef { Id = id, Name = (string)item["snippetMeta"]?["name"] ?? "" });
                added++;
            }

            if (page.Count < BINS_PER_PAGE || added == 0) break;
            lastId = (string)page[page.Count - 1]["record"];
        }
        return result;
    }

    private Task<string> GetBinAsync(string binId)
    {
        return SendJsonBinAsync(() =>
        {
            var req = UnityWebRequest.Get($"{JSONBinManager.BASE_URL}/b/{binId}/latest");
            req.SetRequestHeader("X-Bin-Meta", "false");
            return req;
        });
    }

    private async Task<bool> UpdateBinAsync(string binId, string json)
    {
        string body = await SendJsonBinAsync(() =>
        {
            var req = new UnityWebRequest($"{JSONBinManager.BASE_URL}/b/{binId}", "PUT")
            {
                uploadHandler = new UploadHandlerRaw(Encoding.UTF8.GetBytes(json)),
                downloadHandler = new DownloadHandlerBuffer()
            };
            req.SetRequestHeader("Content-Type", "application/json");
            return req;
        });
        return body != null;
    }

    /// <summary>
    /// Sends a JSONBin request with the master key, spacing requests out and retrying on
    /// 429 / 5xx / network errors with backoff. Returns the body, or null on final failure.
    /// </summary>
    private async Task<string> SendJsonBinAsync(Func<UnityWebRequest> makeRequest)
    {
        for (int attempt = 1; attempt <= _jsonBinMaxAttempts; attempt++)
        {
            float wait = _lastJsonBinRequestTime + _jsonBinMinInterval - Time.realtimeSinceStartup;
            if (wait > 0) await Task.Delay(TimeSpan.FromSeconds(wait));
            _lastJsonBinRequestTime = Time.realtimeSinceStartup;

            using (UnityWebRequest req = makeRequest())
            {
                req.SetRequestHeader("X-Master-Key", JSONBinManager.API_KEY);
                var op = req.SendWebRequest();
                while (!op.isDone) await Task.Yield();

                if (req.result == UnityWebRequest.Result.Success)
                    return req.downloadHandler.text;

                long code = req.responseCode;
                bool retryable = code == 429 || code >= 500 || req.result == UnityWebRequest.Result.ConnectionError;
                if (!retryable || attempt == _jsonBinMaxAttempts)
                {
                    LogErr($"{req.method} {req.url} failed ({code}): {req.error}\n{req.downloadHandler?.text}");
                    return null;
                }

                float backoff = Mathf.Min(60f, 5f * Mathf.Pow(2, attempt - 1));
                if (float.TryParse(req.GetResponseHeader("Retry-After"), out float retryAfter))
                    backoff = Mathf.Max(backoff, retryAfter);
                LogWarn($"{req.method} {req.url} -> {code}; retry {attempt}/{_jsonBinMaxAttempts - 1} in {backoff:0}s.");
                await Task.Delay(TimeSpan.FromSeconds(backoff));
            }
        }
        return null;
    }

    // ---------------- Helpers ---------------- //

    private static readonly HashSet<string> AllowedChangedFields =
        new HashSet<string>(ImageFields) { "images_recovered", "images_recovered_ts" };

    /// <summary>Parses without reinterpreting dates or rounding floats, so a round-trip preserves stored values.</summary>
    private static JObject ParseExact(string json)
    {
        using (var reader = new Newtonsoft.Json.JsonTextReader(new StringReader(json))
               {
                   DateParseHandling = Newtonsoft.Json.DateParseHandling.None,
                   FloatParseHandling = Newtonsoft.Json.FloatParseHandling.Decimal
               })
        {
            return JObject.Load(reader);
        }
    }

    private static bool IsEmpty(JToken t) =>
        t == null || t.Type == JTokenType.Null || (t.Type == JTokenType.String && string.IsNullOrWhiteSpace((string)t));

    /// <summary>
    /// Returns null if 'after' differs from 'before' only in image fields that were empty before
    /// (plus the recovery marker fields); otherwise describes the violation.
    /// </summary>
    private static string CheckOnlyImageFieldsChanged(JObject before, JObject after)
    {
        var names = new HashSet<string>(before.Properties().Select(p => p.Name));
        names.UnionWith(after.Properties().Select(p => p.Name));
        foreach (string name in names)
        {
            if (JToken.DeepEquals(before[name], after[name])) continue;
            if (!AllowedChangedFields.Contains(name))
                return $"field '{name}' would change";
            if (ImageFields.Contains(name) && !IsEmpty(before[name]))
                return $"existing image '{name}' would be overwritten";
        }
        return null;
    }

    private static string Url(JObject run, int i) => (string)run[ImageFields[i]] ?? "";

    private static string Csv(params string[] cols) =>
        string.Join(",", cols.Select(c => "\"" + (c ?? "").Replace("\"", "\"\"") + "\""));

    private static string Sanitize(string s) =>
        string.Concat(s.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));

    private static void Log(string m) => Debug.Log($"[Recovery] {m}");
    private static void LogWarn(string m) => Debug.LogWarning($"[Recovery] {m}");
    private static void LogErr(string m) => Debug.LogError($"[Recovery] {m}");
}
