using UnityEngine;
using UnityEngine.UI;
using UnityEngine.XR.Interaction.Toolkit.UI;
using System.Collections;
using System.Runtime.InteropServices;

public class AvatarCanvasSetup : MonoBehaviour
{
    private const string CANVAS_NAME = "AvatarEditingCanvas";
    private const float POLL_INTERVAL = 0.2f;

    private readonly Vector3 targetPosition = new Vector3(-0.80f, 1.8f, 0.67f);

    void Start()
    {
        StartCoroutine(WaitForCanvas());
    }

    private IEnumerator WaitForCanvas()
    {
        GameObject canvasObject = null;

        while (canvasObject == null)
        {
            canvasObject = GameObject.Find(CANVAS_NAME);
            if (canvasObject == null)
                yield return new WaitForSeconds(POLL_INTERVAL);
        }

        SetupCanvas(canvasObject);
    }

    private void SetupCanvas(GameObject canvasObject)
    {
        // Set position
        // canvasObject.transform.position = targetPosition;

        // Set to World Space
        Canvas canvas = canvasObject.GetComponent<Canvas>();
        if (canvas != null)
        {
            canvas.renderMode = RenderMode.WorldSpace;
            
        }
        else
        {
            Debug.LogError("No Canvas component found on " + CANVAS_NAME);
            return;
        }
        const float scaleFactor = 0.003f;
        canvasObject.transform.localScale = new Vector3(scaleFactor, scaleFactor, scaleFactor);
        RectTransform rectTransform = canvasObject.GetComponent<RectTransform>();
        if (rectTransform != null)
        {
            rectTransform.sizeDelta = new Vector2(390f, 600f);
            rectTransform.localPosition = targetPosition;
        }

        // Swap Graphic Raycaster for Tracked Device Graphic Raycaster
        GraphicRaycaster graphicRaycaster = canvasObject.GetComponent<GraphicRaycaster>();
        if (graphicRaycaster != null)
            Destroy(graphicRaycaster);

        if (canvasObject.GetComponent<TrackedDeviceGraphicRaycaster>() == null)
            canvasObject.AddComponent<TrackedDeviceGraphicRaycaster>();

        Debug.Log("AvatarEditingCanvas setup complete.");
    }
}