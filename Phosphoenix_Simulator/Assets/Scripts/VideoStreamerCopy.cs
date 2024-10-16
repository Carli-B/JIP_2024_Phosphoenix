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

public class VideoStreamerCopy : MonoBehaviour
{
    public Camera vrCamera;  // Reference to the Camera
    //public int textureWidth = 256;   // Width of the frame
    //public int textureHeight = 256;  // Height of the frame
    public SendScript sendScript;

    // Deze variabelen zijn nodig voor het croppen
    //public float cropWidth = 0.8f; // Width of the cropped region (between 0 and 1)
    //public float cropHeight = 0.8f; // Height of the cropped region (between 0 and 1)
    public int cropWidthPixels = 400; // Width of the cropped region in pixels
    public int cropHeightPixels = 400; // Height of the cropped region in pixels
    public Vector2 gazeCoord; // Coordinates of the gaze point (between 0 and 1)
    private Texture2D frameTexture;
    public GameObject targetObject;    // Target object where the cropped texture will be applied, deze aangemaakt voor de andere crop methode
    
    // Deze variable geven de breedte en hoogte van het resizen van het beeld voordat we het naar python sturen
    public int resizeWidth = 256;
    public int resizeHeight = 256;

    public RenderTexture renderTexture;
    private UdpClient udpClient;
    private const string serverIp = "127.0.0.1";
    private const int serverPort = 4903;
    public float transfact = 0.5f;
    private bool sendingActive = false;
    private byte[] frameDelimiter = System.Text.Encoding.ASCII.GetBytes("FRAME_DEL");
    private bool isParallelProcessing = false;
    private byte[] imageData;

    void Start()
    {
        // Get the width and height of the original frame
        int frameWidth = vrCamera.pixelWidth;
        int frameHeight = vrCamera.pixelHeight;
        Debug.Log(frameWidth);
        Debug.Log(frameHeight);

        renderTexture = new RenderTexture(frameWidth, frameHeight, 24);
        vrCamera.targetTexture = renderTexture;
    }

    void Update()
    { 
        if(sendingActive && udpClient != null && !isParallelProcessing)
        {
            captureAndPrepareImage();
            
            isParallelProcessing = true;
            Task.Run(() =>
            {
                sendImageData();
                isParallelProcessing = false;
            });
        
        }
        else if(sendScript.isPythonActive() && !sendingActive)
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

    private void captureAndPrepareImage()
    {
        // Render
        vrCamera.Render();

        // Get the width and height of the original frame
        int frameWidth = renderTexture.width;
        int frameHeight = renderTexture.height;

        // Read the RenderTexture into a Texture2D
        Texture2D texture = new Texture2D(frameWidth, frameHeight, TextureFormat.RGB24, false);
        RenderTexture.active = renderTexture;
        texture.ReadPixels(new Rect(0, 0, frameWidth, frameHeight), 0, 0);
        texture.Apply();

        // Eyetracking: get the gaze point in screen space
        Vector3 gazePoint = VarjoEyeTracking.GetGaze().gaze.forward; // Use gaze data for cropping
        float xCenter = (gazePoint.x + transfact) * frameWidth; // xCenter is in pixels
        float yCenter = (gazePoint.y + transfact) * frameHeight; // yCenter is in pixels

        // Calculate the bottom-left corner of the crop area
        int leftDownCornerX = (int)(xCenter - cropWidthPixels / 2);
        int leftDownCornerY = (int)(yCenter - cropHeightPixels / 2);

        Texture2D croppedTexture = new Texture2D(cropWidthPixels, cropHeightPixels, TextureFormat.RGB24, false);

         // Fill the new texture with black
        Color[] blackPixels = new Color[cropWidthPixels * cropHeightPixels];
        for (int i = 0; i < blackPixels.Length; i++)
        {
            blackPixels[i] = Color.black;
        }
        croppedTexture.SetPixels(blackPixels);

        // Calculate the region of overlap between the crop area and the original image
        int startX = Mathf.Max(0, leftDownCornerX);  // X-coordinate in original texture to start copying from
        int startY = Mathf.Max(0, leftDownCornerY);  // Y-coordinate in original texture to start copying from

        // Calculate how many pixels can be copied in both dimensions (width and height)
        int overlapWidth = Mathf.Min(cropWidthPixels, frameWidth - startX);
        int overlapHeight = Mathf.Min(cropHeightPixels, frameHeight - startY);

        // Ensure the overlap width and height do not exceed the bounds of the cropped texture
        overlapWidth = Mathf.Min(overlapWidth, cropWidthPixels - Mathf.Max(0, -leftDownCornerX));
        overlapHeight = Mathf.Min(overlapHeight, cropHeightPixels - Mathf.Max(0, -leftDownCornerY));

        // If there is any overlap between the crop area and the original texture
        if (overlapWidth > 0 && overlapHeight > 0)
        {
            // Get the pixels from the original texture that overlap
            Color[] pixels = texture.GetPixels(startX, startY, overlapWidth, overlapHeight);

            // Calculate the offset where to place the copied pixels in the new texture
            int offsetX = Mathf.Max(0, -leftDownCornerX);  // Offset in cropped texture where the pixels will be placed
            int offsetY = Mathf.Max(0, -leftDownCornerY);

            // Copy the pixels into the cropped texture at the calculated offset
            croppedTexture.SetPixels(offsetX, offsetY, overlapWidth, overlapHeight, pixels);
        }

        croppedTexture.Apply();

        // Convert the cropped texture into a sprite
        Sprite croppedSprite = Sprite.Create(croppedTexture, new Rect(0, 0, croppedTexture.width, croppedTexture.height), new Vector2(0.5f, 0.5f));

        // Assign the cropped sprite to the target object's SpriteRenderer
        if (targetObject != null)
        {
            SpriteRenderer spriteRenderer = targetObject.GetComponent<SpriteRenderer>();
            if (spriteRenderer != null)
            {
                spriteRenderer.sprite = croppedSprite;
            }
        }

        // Convert the cropped texture to grayscale (1 byte per pixel)
        Texture2D grayscaleTexture = new Texture2D(resizeWidth, resizeHeight, TextureFormat.R8, false); // R8 = Grayscale
        for (int y = 0; y < resizeHeight; y++)
        {
            for (int x = 0; x < resizeWidth; x++)
            {
                Color pixelColor = croppedTexture.GetPixel(x, y);
                float grayValue = pixelColor.grayscale; // Get grayscale value
                grayscaleTexture.SetPixel(x, y, new Color(grayValue, grayValue, grayValue));
            }
        }
        grayscaleTexture.Apply();

        // Get raw grayscale image data (1 byte per pixel)
        imageData = grayscaleTexture.GetRawTextureData();

        // Clean up
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
        byte[] frameDelimiter = System.Text.Encoding.ASCII.GetBytes("FRAME_DEL");
        udpClient.Send(frameDelimiter, frameDelimiter.Length);
    }

    public bool is_sending_active()
    {
        return sendingActive;
    }

    void OnApplicationQuit()
    { 
        udpClient.Close();
    }
}