//#define MEASURE_TIMES //uncomment to measure times

using System.Diagnostics;
using System.IO;
using UnityEngine;
using System.Runtime.InteropServices;
using System;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Collections.Generic;

using Debug = UnityEngine.Debug;
using SysDebug = System.Diagnostics.Debug;

public class PhospheneRenderer : MonoBehaviour
{
    public FOVSelector fovSelector;     // To communicate with the scripts sending cropped images to Python
    private Config config;              // Data structure for storing configuration

    // Variables that will store the resolution of the received phosphene images
    private int cropWidthPixels;
    private int cropHeightPixels;

    // variables for receiving data from the python script
    private UdpClient udpClient;
    private int port = 9003;
    private byte[] frameDelimiter = System.Text.Encoding.ASCII.GetBytes("FRAME_DEL");

    // Variables for activating the Python script
    private Process pythonProcess;
    private string shutdownFilePath;
    private string pythonDirPath;

    // Data storage and buffering
    public RenderTexture renderTexture; // Visible RenderTexture where phosphenes will be applied
    private Texture2D texture;          // Texture for storing phosphene images before applying them
    private byte[] receivedImageData;   // Variable to buffer incoming data

    // Flags
    private bool UdpClientActive = false;
    private bool pythonActive = false;
    private bool newImageAvailable = false;
    private bool receivingImages = false;
    private bool kappenNu = false;

#if MEASURE_TIMES
    // Variables for recording computation times
    private string filePathR2;
    private string filePathRend;
    private int measurementCounterR2 = 0;
    private int measurementCounterRend = 0;
    private const int maxMeasurements = 1000;
#endif

    void Start()
    {
        pythonDirPath = Application.streamingAssetsPath;

#if MEASURE_TIMES
        // Define the file path for storing measurements in the user's Documents folder
        string documentsPath = System.Environment.GetFolderPath(System.Environment.SpecialFolder.MyDocuments);
        filePathR2 = Path.Combine(documentsPath, "measurements_r2.json"); 
        filePathRend = Path.Combine(documentsPath, "measurements_rend.json"); 
        Debug.Log($"The timing files will be saved to: {filePathR2} and {filePathRend}");
#endif

        // Remove Shutdown file to ensure python will keep running
        shutdownFilePath = $"{pythonDirPath}/shutdown.txt";
        if (File.Exists(shutdownFilePath))
        {
            File.Delete(shutdownFilePath);
        }

        // Load config with conda environment name and python script name
        LoadConfig();

        // resolulu
        cropWidthPixels = fovSelector.return_cropWidthPixels();
        cropHeightPixels = fovSelector.return_cropHeightPixels();

        // Initialize variables
        texture = new Texture2D(cropWidthPixels, cropHeightPixels, TextureFormat.RGBA32, false);
        receivedImageData = new byte[cropWidthPixels * cropHeightPixels * 4]; //correct resolulu * 4 channels

        // set color to white
        for(int i = 0; i < cropWidthPixels * cropHeightPixels; i++)
        {
            receivedImageData[i*4] = 255;
            receivedImageData[i*4 + 1] = 255;
            receivedImageData[i*4 + 2] = 255;
            receivedImageData[i*4 + 3] = 255;
        }

        // Make UDP port ready to receive phosphene data
        udpClient = new UdpClient(port);
        UdpClientActive = true;
        Debug.Log("phosphene socket (in) working from Unity side");
        
        // Start the python script
        RunPythonScript();
        Thread.Sleep(3000);

        // Flag to the sender that python is ready and active
        pythonActive = true;
    }

    void Update()
    {
        if (newImageAvailable)
        {
#if MEASURE_TIMES
            Stopwatch timer = new Stopwatch();
            timer.Start();
#endif
            ApplyReceivedImage(); // This line of code is where the actual image is applied
            newImageAvailable = false;
#if MEASURE_TIMES
            timer.Stop();
            AppendMeasurementToJsonFile(timer.ElapsedMilliseconds, ref measurementCounterRend, filePathRend);
#endif
        }

        if (UdpClientActive && fovSelector.is_sending_active() && !receivingImages)
        {
            receivingImages = true;
            // Start background task for receiving image
            Task.Run(() => ReceiveImageData());
        }
    }

    // Background thread for receiving image data
    private void ReceiveImageData()
    {
        IPEndPoint remoteEndPoint = new IPEndPoint(IPAddress.Any, port);

        while (true)  // Continuously listen for UDP data
        {   
            if(kappenNu)
            {
                break;
            }

            // Buffer for the received grayscale image
            byte[] receivedData = new byte[cropWidthPixels * cropHeightPixels];
            int received = 0;
            bool newFrameStarted = false;

#if MEASURE_TIMES
            bool started_recoring_times = false;
            Stopwatch timer = new Stopwatch();
#endif
            // Continuously receive data until a full frame is reconstructed
            while (received < receivedData.Length)
            {
                try
                {
                    byte[] packet = udpClient.Receive(ref remoteEndPoint);
#if MEASURE_TIMES
                    if(!started_recoring_times)
                    {
                        started_recoring_times = true;
                        timer.Start();
                    }
#endif
                    // Check for frame delimiter
                    if (packet.Length == frameDelimiter.Length)
                    { 
                        if (CompareArrays(packet, frameDelimiter))
                        {
                            newFrameStarted = true;
                            received = 0;  // Start receiving a new frame
                            continue; // do not copy frame delimiter into data!
                        }
                    }

                    if (newFrameStarted)
                    {
                        int bytesToCopy = Math.Min(packet.Length, receivedData.Length - received);
                        
                        // Copy packet data into the frame buffer
                        Array.Copy(packet, 0, receivedData, received, bytesToCopy);
                        received += bytesToCopy;
                    }
                }
                catch (SocketException ex)
                {
                    Debug.LogError("Error receiving UDP data: " + ex.Message);
                }
            }

            // Store the received frame in the shared buffer and signal the main thread
            for(int i = 0; i < receivedData.Length; i++)
            {
                receivedImageData[i*4 + 3] = receivedData[receivedData.Length - i - 1];
            }

            // Notify the main thread that a new image is ready
            newImageAvailable = true;  
#if MEASURE_TIMES
            timer.Stop();
            AppendMeasurementToJsonFile(timer.ElapsedMilliseconds, ref measurementCounterR2, filePathR2);
#endif
        }
    }

    // Main thread function to apply the received image to a texture
    private void ApplyReceivedImage()
    {
        texture.LoadRawTextureData(receivedImageData);
        texture.Apply();
        Graphics.Blit(texture, renderTexture);
    }

    private void LoadConfig()
    {
        string configPath = pythonDirPath + "/config.json";
        if (File.Exists(configPath))
        {
            string json = File.ReadAllText(configPath);
            config = JsonUtility.FromJson<Config>(json);
        }
        else
        {
            Debug.LogError("Config file not found: " + configPath);
        }
    }

    private void RunPythonScript()
    {
        if (config == null)
        {
            Debug.LogError("Config not loaded.");
            return;
        }

        ProcessStartInfo processInfo = new ProcessStartInfo();
        string scriptPath = Path.Combine(pythonDirPath, config.pythonScript);

        if (config.operatingSystem == "macOS")
        {
            string condaActivate = "source " + config.baseCondaPath + "/bin/activate " + config.condaEnvPath;
            processInfo.FileName = "/bin/bash";
            processInfo.Arguments = $"-c \"{condaActivate} && exec python {scriptPath} '{shutdownFilePath}' '{pythonDirPath}'\" {cropWidthPixels} {cropHeightPixels}";
        }
        else if (config.operatingSystem == "Windows")
        {
            string condaActivate = "conda activate " + config.condaEnvPath;
            processInfo.FileName = "cmd.exe";
            processInfo.Arguments = $"/C \"{condaActivate} && python -u \"{scriptPath}\" \"{shutdownFilePath}\" \"{pythonDirPath}\" {cropWidthPixels} {cropHeightPixels}\"";
        }
        else
        {
            Debug.LogError("Unknown operating system");
            return;
        }

        processInfo.RedirectStandardOutput = true;
        processInfo.RedirectStandardError = true;
        processInfo.UseShellExecute = false;
        processInfo.CreateNoWindow = true;

        pythonProcess = new Process { StartInfo = processInfo };

        pythonProcess.OutputDataReceived += (sender, args) =>
        {
            if (!string.IsNullOrEmpty(args.Data))
            {
                Debug.Log(args.Data);
            }
        };

        pythonProcess.ErrorDataReceived += (sender, args) =>
        {
            if (!string.IsNullOrEmpty(args.Data))
            {
                Debug.LogError(args.Data);
            }
        };

        pythonProcess.Start();
        pythonProcess.BeginOutputReadLine();
        pythonProcess.BeginErrorReadLine();
    }

    private void OnDestroy()
    {
        // flag to stop background process that receives phosphene images
        kappenNu = true;

        // close UDP socket
        udpClient.Close();
        UdpClientActive = false;
        
        // create file to tell Python to stop
        File.WriteAllText(shutdownFilePath, "shutdown");
    }

    public bool isPythonActive()
    {
        return pythonActive;
    }

    private bool CompareArrays(byte[] a1, byte[] a2)
    {
        if (a1.Length != a2.Length)
        {
            return false;
        }

        for (int i = 0; i < a1.Length; i++)
        {
            if (a1[i] != a2[i])
                return false;
        }
        return true;
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

// This class will store some configuration settings
[System.Serializable]
public class Config
{
    public string baseCondaPath;
    public string condaEnvPath;
    public string pythonScript;
    public string operatingSystem; //Supported: "Windows" and "macOS"
}