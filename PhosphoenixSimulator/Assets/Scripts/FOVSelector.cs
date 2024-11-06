//#define MEASURE_TIMES //uncomment to measure times

using UnityEngine;
using UnityEngine.UI;
using Varjo;
using Varjo.XR;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.IO;
using System;
using System.Threading.Tasks;
using System.Diagnostics;
using System.Collections.Generic;
using YamlDotNet.Serialization;
using YamlDotNet.RepresentationModel;

using Debug = UnityEngine.Debug;
using SysDebug = System.Diagnostics.Debug;

public class FOVSelector : MonoBehaviour
{
    public Camera vrCamera;                     // Reference to the Camera
    public PhospheneRenderer phospheneRenderer; // To communicate with the scripts rendering the phosphenes

    // These (constant) variables are needed for cropping
    private float view_angle = 16;
    private float fovDegreeVarjoHori = 115;
    private float fovDegreeVarjoVert = 90;
    private float resolutionPixelsHori = 2880;
    private float resolutionPixelsVert = 2720;
    private int cropWidthPixels;                // Width of the cropped region in pixels
    private int cropHeightPixels;               // Height of the cropped region in pixels
    private float transfact = 0.5f;             // For converting coordinates of the gaze point

    //These variables are needed during the cropping
    public Vector2 gazeCoord;                   // Coordinates of the gaze point (between 0 and 1)
    public RenderTexture renderTexture;         // renderTexture for storing the live camera data
    Color[] blackPixels;                        // Pre-rendered cropped image with black pixels only (Created in Start())
    private byte[] imageData;                   // Variable to store final data that is being transmitted

    // Variables for data transmission to the python script
    private UdpClient udpClient;
    private const string serverIp = "127.0.0.1";
    private const int serverPort = 4906;
    private byte[] frameDelimiter = System.Text.Encoding.ASCII.GetBytes("FRAME_DEL");
    private byte[] exitCode = System.Text.Encoding.ASCII.GetBytes("EXIT_CODE");

    //Flags
    private bool sendingActive = false;
    private bool isParallelProcessing = false;

#if MEASURE_TIMES
    // Variables for recording computation times
    private string filePathS1;
    private string filePathPrep;
    private int measurementCounterS1 = 0;
    private int measurementCounterPrep = 0;
    private const int maxMeasurements = 1000;
#endif

    void Start()
    {
#if MEASURE_TIMES
        // Define the file path for storing measurements in the user's Documents folder
        string documentsPath = System.Environment.GetFolderPath(System.Environment.SpecialFolder.MyDocuments);
        filePathS1 = Path.Combine(documentsPath, "measurements_s1.json"); 
        filePathPrep = Path.Combine(documentsPath, "measurements_prep.json"); 
        Debug.Log($"The timing files will be saved to: {filePathS1} and {filePathPrep}");
#endif

        // Get the width and height of the original frame
        int frameWidth = vrCamera.pixelWidth;
        int frameHeight = vrCamera.pixelHeight;

        // Determine the resolution of the cropped image to correspond with the correct field of view
        cropWidthPixels = return_cropWidthPixels();
        cropHeightPixels = return_cropHeightPixels();

        // Initialize a black image of size of the cropped image to fill in borders easily 
        blackPixels = new Color[cropWidthPixels * cropHeightPixels];
        for (int i = 0; i < blackPixels.Length; i++)
        {
            blackPixels[i] = Color.black;
        }

        // Link camera and texture where the live camera data is applied
        renderTexture = new RenderTexture(frameWidth, frameHeight, 24);
        vrCamera.targetTexture = renderTexture;
    }

    void Update()
    { 
        if(sendingActive && udpClient != null && !isParallelProcessing)
        {
#if MEASURE_TIMES
            Stopwatch timer = new Stopwatch();
            timer.Start();
#endif
            captureAndPrepareImage(); // This is where the actual cropping happens
#if MEASURE_TIMES
            timer.Stop();
            AppendMeasurementToJsonFile(timer.ElapsedMilliseconds, ref measurementCounterPrep, filePathPrep);
#endif 
            isParallelProcessing = true;

            Task.Run(() =>
            {
#if MEASURE_TIMES
                Stopwatch timer = new Stopwatch();
                timer.Start();
#endif
                sendImageData();
#if MEASURE_TIMES
                timer.Stop();
                AppendMeasurementToJsonFile(timer.ElapsedMilliseconds, ref measurementCounterS1, filePathS1);
#endif
                isParallelProcessing = false;
            });
            
        }
        else if(phospheneRenderer.isPythonActive() && !sendingActive)
        {
            // Set up the client UDP connection to the Python server
            try
            {
                udpClient = new UdpClient(serverIp, serverPort);
                Debug.Log("camera socket (out) working from Unity side");
            }
            catch (Exception e)
            {
                Debug.LogError("Connection error: " + e.Message);
            }
            // Python is running and ready to receive data, so setup sending end of socket 
            sendingActive = true;
        }
    }

    public int return_cropWidthPixels()
    {
        // The width of the cropped image in pixels corresponding to predefined vof assuming no camera distortion
        return (int)(Math.Round(view_angle / fovDegreeVarjoHori * resolutionPixelsHori));
    }

    public int return_cropHeightPixels()
    {
        // The height of the cropped image in pixels corresponding to predefined vof assuming no camera distortion
        return (int)(Math.Round(view_angle / fovDegreeVarjoVert * resolutionPixelsVert));
    }

    private void captureAndPrepareImage()
    {
        // Render and capture the frame
        vrCamera.Render();
        int frameWidth = renderTexture.width;
        int frameHeight = renderTexture.height;

        // Capture the RenderTexture into a Texture2D
        Texture2D texture = new Texture2D(frameWidth, frameHeight, TextureFormat.RGB24, false);
        RenderTexture.active = renderTexture;
        texture.ReadPixels(new Rect(0, 0, frameWidth, frameHeight), 0, 0);
        texture.Apply();

        // Calculate gaze-based crop position in pixels
        Vector3 gazePoint = VarjoEyeTracking.GetGaze().gaze.forward;
        float xCenter = (gazePoint.x + transfact) * frameWidth;
        float yCenter = (gazePoint.y + transfact) * frameHeight;

        // Define crop area
        int leftDownCornerX = Mathf.FloorToInt(xCenter - cropWidthPixels / 2);
        int leftDownCornerY = Mathf.FloorToInt(yCenter - cropHeightPixels / 2);

        // Initialize a black-filled cropped texture
        Texture2D croppedTexture = new Texture2D(cropWidthPixels, cropHeightPixels, TextureFormat.RGB24, false);
        
        // Initialize a black-filled cropped texture
        croppedTexture.SetPixels(blackPixels);

        // Calculate overlap between crop area and original image
        int startX = Mathf.Max(0, leftDownCornerX);
        int startY = Mathf.Max(0, leftDownCornerY);
        int overlapWidth = Mathf.Clamp(cropWidthPixels, 0, frameWidth - startX);
        int overlapHeight = Mathf.Clamp(cropHeightPixels, 0, frameHeight - startY);

        // Copy overlapped pixels into cropped texture if any overlap exists
        if (overlapWidth > 0 && overlapHeight > 0)
        {
            Color[] pixels = texture.GetPixels(startX, startY, overlapWidth, overlapHeight);
            croppedTexture.SetPixels(
                Mathf.Max(0, -leftDownCornerX),
                Mathf.Max(0, -leftDownCornerY),
                overlapWidth,
                overlapHeight,
                pixels
            );
        }
        croppedTexture.Apply();

        // Convert cropped texture to grayscale and populate imageData
        Texture2D grayscaleTexture = new Texture2D(cropWidthPixels, cropHeightPixels, TextureFormat.R8, false);
        for (int y = 0; y < cropHeightPixels; y++)
        {
            for (int x = 0; x < cropWidthPixels; x++)
            {
                float grayValue = croppedTexture.GetPixel(x, y).grayscale;
                grayscaleTexture.SetPixel(x, y, new Color(grayValue, grayValue, grayValue));
            }
        }
        grayscaleTexture.Apply();
        imageData = grayscaleTexture.GetRawTextureData();

        // Cleanup
        RenderTexture.active = null;
        Destroy(texture);
        Destroy(croppedTexture);
        Destroy(grayscaleTexture);
    }

    private void sendImageData()
    {
        
        if (imageData == null)
        {
            return;
        }

        // Send the data in chunks over UDP
        int CHUNK_SIZE = 1024;
        for (int i = imageData.Length - CHUNK_SIZE; i >= 0; i -= CHUNK_SIZE)
        {
            int chunkSize = Mathf.Min(CHUNK_SIZE, imageData.Length - i);
            byte[] chunk = new byte[chunkSize];
            for(int j = 0; j < chunkSize; j++)
            {
                chunk[j] = imageData[i + chunkSize - 1 - j];
            }
            //Array.Copy(imageData, i, chunk, 0, chunkSize);
            udpClient.Send(chunk, chunk.Length);
        }

        // Send frame delimiter
        udpClient.Send(frameDelimiter, frameDelimiter.Length);
        
    }

    public bool is_sending_active()
    {
        return sendingActive;
    }

    void OnDestroy()
    {     
        udpClient.Send(exitCode, exitCode.Length);  
        udpClient.Close();
    }

#if MEASURE_TIMES
    private void AppendMeasurementToJsonFile(long measurement, ref int measurementCounter, string filePath)
    {
        // Check if the file exists and if the measurementCounter is 0
        if (measurementCounter == 0)
        {
            // Create a new file and add the first measurement
            CreateNewFileAndAddFirstMeasurement(measurement, filePath);
            measurementCounter = 1;
        }
        else
        {
            // Append to existing file if the measurementCounter is larger than 0
            if (measurementCounter < maxMeasurements)
            {
                AppendMeasurement(measurement, filePath);
                measurementCounter++;
            }
            else if(measurementCounter == maxMeasurements)
            {
                Debug.Log($"File stored at {filePath} is complete");
            }
        }
    }

    private void CreateNewFileAndAddFirstMeasurement(long measurement, string filePath)
    {
        // Create a list to hold the measurements
        List<string> measurements = new List<string> { measurement.ToString() };

        // Write the list of measurements to the JSON file manually
        string json = "{\"measurements\":[" + "\"" + measurement + "\"" + "]}";
        File.WriteAllText(filePath, json);
    }

    private void AppendMeasurement(long measurement, string filePath)
    {
        // Read the existing JSON file as text and find where to append the new data
        string json = File.ReadAllText(filePath);

        // Insert the new measurement into the JSON string manually
        int insertIndex = json.LastIndexOf(']');
        if (insertIndex > 0)
        {
            string newJson = json.Substring(0, insertIndex) + ",\"" + measurement + "\"" + json.Substring(insertIndex);
            File.WriteAllText(filePath, newJson);
        }
    }
#endif
}