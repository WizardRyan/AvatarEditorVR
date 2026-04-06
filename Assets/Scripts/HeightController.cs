using UnityEngine;

public class HeightController : MonoBehaviour
{
    [Tooltip("The Camera Offset object from your XR Origin hierarchy")]
    [SerializeField] private Transform _xrCameraOffset;

    [Tooltip("GameObjects whose Y position will track the headset height")]
    [SerializeField] private GameObject[] _trackedObjects;

    private float[] _yOffsets;

    void Start()
    {
        _yOffsets = new float[_trackedObjects.Length];
        float headsetY = _xrCameraOffset != null ? _xrCameraOffset.position.y : 0f;

        for (int i = 0; i < _trackedObjects.Length; i++)
        {
            if (_trackedObjects[i] == null) continue;
            _yOffsets[i] = _trackedObjects[i].transform.position.y - headsetY;
        }
    }

    void Update()
    {
        if (_xrCameraOffset == null) return;

        float headsetY = _xrCameraOffset.position.y;

        for (int i = 0; i < _trackedObjects.Length; i++)
        {
            if (_trackedObjects[i] == null) continue;
            Vector3 pos = _trackedObjects[i].transform.position;
            pos.y = headsetY + _yOffsets[i];
            _trackedObjects[i].transform.position = pos;
        }
    }
}
