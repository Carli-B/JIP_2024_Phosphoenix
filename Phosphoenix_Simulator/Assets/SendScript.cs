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

using Debug = UnityEngine.Debug;
using SysDebug = System.Diagnostics.Debug;

public class SendScript : MonoBehaviour
{
    public RenderTexture renderTexture;
    private int port = 9003;
    private UdpClient udpClient;
    private Texture2D texture;
    private byte[] frameDelimiter = System.Text.Encoding.ASCII.GetBytes("FRAME_DEL");
    private bool UdpClientActive = false;
    private bool pythonActive = false;

    private Config config;
    private Process pythonProcess;
    private string shutdownFilePath;
    private string pythonDirPath;
    public VideoStreamerCopy videoStreamerCopy;

    private byte[] receivedImageData;
    private bool newImageAvailable = false;
    private bool receivingImages = false;

    void Start()
    {
        // Remove Shutdown file to ensure python will keep running
        shutdownFilePath = Path.Combine(Application.persistentDataPath, "shutdown.txt");
        if (File.Exists(shutdownFilePath))
        {
            File.Delete(shutdownFilePath);
        }
        pythonDirPath = Application.streamingAssetsPath;

        // Load config with conda environment name and python script name
        LoadConfig();
        Debug.Log("phosphene socket (in) working from Unity side");

        // Initialize variables
        texture = new Texture2D(256, 256, TextureFormat.RGBA32, false);
        receivedImageData = new byte[256*256*4]; //256*256 pixels with 4 channels
        // set color to white
        for(int i = 0; i < 256*256; i++)
        {
            receivedImageData[i*4] = 255;
            receivedImageData[i*4 + 1] = 255;
            receivedImageData[i*4 + 2] = 255;
            receivedImageData[i*4 + 3] = 255;
        }

        // Make UDP port ready to receive phosphene data
        udpClient = new UdpClient(port);
        UdpClientActive = true;
        
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
            ApplyReceivedImage();
            newImageAvailable = false;
        }

        if (UdpClientActive && videoStreamerCopy.is_sending_active() && !receivingImages)
        {
            receivingImages = true;
            // Start background task for receiving image
            Task.Run(() => ReceiveImageData());
        }
        
        /*
        // RECEIVE PHOSPHENE DATA AND APPLY TO TEXTURE
        if (UdpClientActive && videoStreamerCopy.is_sending_active())
        {
            Debug.Log("I want to receive phosphenes but I am not allowed to :'(");
            
            IPEndPoint remoteEndPoint = new IPEndPoint(IPAddress.Any, port);
            try
            {
                // buffer:
                byte[] receivedData = new byte[256 * 256];

                int received = 0;
                bool newFrameStarted = false;
                while (received < receivedData.Length)
                {
                    byte[] packet = udpClient.Receive(ref remoteEndPoint);
                    if (packet.Length == frameDelimiter.Length && CompareArrays(packet, frameDelimiter))
                    {
                        newFrameStarted = true;
                        received = 0;
                    }
                    else if (newFrameStarted)
                    {
                        // Copy the packet data into the frame buffer
                        Array.Copy(packet, 0, receivedData, received, packet.Length);
                        received += packet.Length;
                    }
                }

                print("A frame has been received");

                texture.LoadRawTextureData(receivedData);
                texture.Apply();

                Graphics.Blit(texture, renderTexture);
            }
            catch (Exception e)
            {
                Debug.Log("Error receiving UDP data: " + e.Message);
            }
            
        }
        */
    }

    // Background thread for receiving image data
    private void ReceiveImageData()
    {
        IPEndPoint remoteEndPoint = new IPEndPoint(IPAddress.Any, port);

        while (true)  // Continuously listen for UDP data
        {   
            // Buffer for the received image (256x256 grayscale)
            byte[] receivedData = new byte[256 * 256];
            int received = 0;
            bool newFrameStarted = false;

            // Continuously receive data until a full frame is reconstructed
            while (received < receivedData.Length)
            {
                byte[] packet = udpClient.Receive(ref remoteEndPoint);

                // Check for frame delimiter
                if (packet.Length == frameDelimiter.Length && CompareArrays(packet, frameDelimiter))
                {
                    newFrameStarted = true;
                    received = 0;  // Start receiving a new frame
                }
                else if (newFrameStarted)
                {
                    // Copy packet data into the frame buffer
                    Array.Copy(packet, 0, receivedData, received, packet.Length);
                    received += packet.Length;
                }
            }

        // Store the received frame in the shared buffer and signal the main thread
        for(int i = 0; i < receivedData.Length; i++)
        {
            receivedImageData[i*4 + 3] = receivedData[i];
        }
        
        newImageAvailable = true;  // Notify the main thread that a new image is ready
        }
    }

    // Main thread function to apply the received image to a texture
    private void ApplyReceivedImage()
    {
        texture.LoadRawTextureData(receivedImageData);
        texture.Apply();
        Graphics.Blit(texture, renderTexture);
        /*
        // Check if the image in receivedImageData is of correct format
        if (receivedImageData == null)
        {
            return;
        }
        else if (receivedImageData.Length != 256*256)
        {
            Debug.Log("Error: Image data was not of correct length!");
            return;
        }

        // If everything is correct: 
        // use receivedImageData as alpha channel for solid white plane

        Color32[] pixelColors = new Color32[256*256];
        for (int i = 0; i < receivedImageData.Length; i++)
        {
            // set RGB to (255, 255, 255) (white) and alpha to grayscale value
            pixelColors[i] = new Color32(255, 255, 255, receivedImageData[i]);
        }

        texture.SetPixels32(pixelColors);
        texture.Apply();
        Graphics.Blit(texture, renderTexture);
        */
    }

    public bool isPythonActive()
    {
        return pythonActive;
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
            processInfo.Arguments = $"-c \"{condaActivate} && exec python {scriptPath} '{shutdownFilePath}' '{pythonDirPath}'\"";
        }
        else if (config.operatingSystem == "Windows")
        {
            string condaActivate = "conda activate " + config.condaEnvPath;
            processInfo.FileName = "cmd.exe";
            processInfo.Arguments = $"/C \"{condaActivate} && python -u \"{scriptPath}\" \"{shutdownFilePath}\" \"{pythonDirPath}\"\"";
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

    private void OnApplicationQuit()
    {
        udpClient.Close();
        UdpClientActive = false;
        
        File.WriteAllText(shutdownFilePath, "shutdown");
        if (pythonProcess != null && !pythonProcess.HasExited)
        {
            Debug.Log("Waiting for Shutdown...");
            pythonProcess.WaitForExit(3000);  
        }
        if (pythonProcess != null && !pythonProcess.HasExited)
        {
            Debug.Log("Forcefull shutdown :(");
            pythonProcess.Kill();
        }
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