using UnityEngine;
using UnityEngine.EventSystems;
using TMPro;

public class VRInputField : MonoBehaviour, IPointerClickHandler
{
    [SerializeField] private TMP_InputField inputField;
    [SerializeField] private VRKeyboard keyboard;
    [SerializeField] private MyDebugLogger logger;

    public void OnPointerClick(PointerEventData eventData)
    {
        logger.AddLog("Input field clicked");
        keyboard.Show(inputField);
    }
}