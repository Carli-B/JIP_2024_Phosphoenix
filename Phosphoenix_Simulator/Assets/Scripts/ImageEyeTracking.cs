using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
using System.IO;
using Varjo;
using Varjo.XR;

public class ImageEyeTracking : MonoBehaviour
{

    public RawImage joejoe;
    private Vector2 newPivot;
    private float transfact = 0.0f;

    // Start is called before the first frame update
    void Start()
    {
        Vector3 gazePoint = VarjoEyeTracking.GetGaze().gaze.forward;

        
        // Uitsnede berekenen
        float xCenter = (gazePoint.x + transfact) * Screen.width;
        float yCenter = (gazePoint.y + transfact) * Screen.height;

        newPivot[0] = xCenter;
        newPivot[1] = yCenter;

        joejoe.rectTransform.anchoredPosition = newPivot;
    }

    // Update is called once per frame
    void Update()
    {
        Vector3 gazePoint = VarjoEyeTracking.GetGaze().gaze.forward;

        
        // Uitsnede berekenen
        float xCenter = (gazePoint.x + transfact) * Screen.width;
        float yCenter = (gazePoint.y + transfact) * Screen.height;

        newPivot[0] = xCenter;
        newPivot[1] = yCenter;

        joejoe.rectTransform.anchoredPosition = newPivot;
    }
}
