using UnityEngine;
using System.Collections.Generic;

public class MyDebugLogger : MonoBehaviour
{
    [SerializeField] private TMPro.TMP_Text logText;
    [SerializeField] private int maxLines = 8;

    private Queue<string> lines = new Queue<string>();

    public void AddLog(string text)
    {
        lines.Enqueue(text);

        if (lines.Count > maxLines)
            lines.Dequeue();

        logText.text = string.Join("\n", lines);
    }
}