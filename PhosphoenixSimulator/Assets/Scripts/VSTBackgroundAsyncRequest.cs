using System;
using System.Collections;
using Varjo.XR;

using UnityEngine;
using UnityEngine.UI;
using UnityEngine.XR.ARSubsystems;
using UnityEngine.XR.Management;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;

using static UnityEngine.XR.ARSubsystems.XRCpuImage;


public class VSTBackgroundAsyncRequest : MonoBehaviour
{
    public enum SupportedTargetFormat
    {
        RGBA32,
        R8
    }

    public SupportedTargetFormat settingTargetFormat;

    private VarjoCameraSubsystem cameraSubsystem;
    private Texture2D[] distortedTextures = new Texture2D[2];
    public RenderTexture[] undistortedTextures = new RenderTexture[2];
    public Material skyMaterial;

    private TextureFormat targetFormat;

    private static XRLoader GetActiveLoader()
    {
        if (XRGeneralSettings.Instance != null && XRGeneralSettings.Instance.Manager != null)
        {
            return XRGeneralSettings.Instance.Manager.activeLoader;
        }

        return null;
    }

    private void OnEnable()
    {
        VarjoLoader loader = GetActiveLoader() as Varjo.XR.VarjoLoader;
        if (!loader)
        {
            Debug.Log("Can't get instance of VarjoLoader!");
            return;
        }

        cameraSubsystem = loader.cameraSubsystem as VarjoCameraSubsystem;
        if (cameraSubsystem != null)
        {
            Debug.Log("Starting camera subsystem...");
            cameraSubsystem.Start();
            if (!cameraSubsystem.EnableColorStream())
                Debug.Log("Can't start color stream!");
        }
        else
            Debug.Log("Can't start camera subsystem!");

        VarjoCameraSubsystem.ImagesAllocator = Allocator.TempJob;
    }

    private void OnDisable()
    {
        if (cameraSubsystem != null)
        {
            Debug.Log("Stopping camera subsystem...");
            cameraSubsystem.DisableColorStream();
            cameraSubsystem.Stop();
            cameraSubsystem = null;
        }
    }

    void Update()
    {
        if (cameraSubsystem == null || !cameraSubsystem.IsColorStreamEnabled) return;

        if (!cameraSubsystem.TryAcquireLatestCpuImage(out XRCpuImage image)) return;

        switch (settingTargetFormat)
        {
            case SupportedTargetFormat.R8:
                targetFormat = TextureFormat.R8;
                skyMaterial.SetFloat("_GrayScale", 1f);
                break;
            case SupportedTargetFormat.RGBA32:
                targetFormat = TextureFormat.RGBA32;
                skyMaterial.SetFloat("_GrayScale", 0f);
                break;
            default:
                return;
        }

        StartCoroutine(ProcessImage(image));
    }

    IEnumerator ProcessImage(XRCpuImage image)
    {
        var conversionParams = new XRCpuImage.ConversionParams(image, targetFormat);
        var request = image.ConvertAsync(conversionParams);
        var CPUImageSizeBytesFor1Channel = image.GetConvertedDataSize(conversionParams.outputDimensions, targetFormat);

        // Wait for the conversion to complete.
        while (!request.status.IsDone())
            yield return null;

        // Check status to see if the conversion completed successfully.
        if (request.status != XRCpuImage.AsyncConversionStatus.Ready)
        {
            // Something went wrong.
            Debug.LogErrorFormat("ConvertAsync failed with status {0}", request.status);

            // Dispose even if there is an error.
            request.Dispose();
            yield break;
        }

        // Image data is ready

            var array = request.GetData<byte>();
            var dimensions = request.conversionParams.outputDimensions;

            VarjoCameraSubsystem.Channel = VarjoStreamChannel.Left;
            SetImage(dimensions, array, 0, CPUImageSizeBytesFor1Channel);

            VarjoCameraSubsystem.Channel = VarjoStreamChannel.Right;
            SetImage(dimensions, array, 1, CPUImageSizeBytesFor1Channel);

        // Need to dispose the request to delete resources associated
        // with the request, including the raw data.
        request.Dispose();
        image.Dispose();
    }

    void SetImage(Vector2Int dimensions, NativeArray<byte> array, int offset, int dataSizeBytes)
    {
        int i = (int)VarjoCameraSubsystem.Channel;

        if (distortedTextures[i] == null || distortedTextures[i].format != targetFormat)
        {
            distortedTextures[i] = new Texture2D(dimensions.x, dimensions.y, targetFormat, false);
            distortedTextures[i].name = i == 0 ? "Left distorted camera image" : "Right distorted camera image";
        }
        else if (distortedTextures[i].width != dimensions.x || distortedTextures[i].height != dimensions.y)
        {
            distortedTextures[i].Reinitialize(dimensions.x, dimensions.y);
        }

        unsafe
        {
            byte* convertedData = ((byte*)NativeArrayUnsafeUtility.GetUnsafeBufferPointerWithoutChecks(array)) + dataSizeBytes * offset;
            distortedTextures[i].LoadRawTextureData((IntPtr)convertedData, dataSizeBytes);
        }

        distortedTextures[i].Apply();

        VarjoCameraSubsystem.Channel = (VarjoStreamChannel)i;
        if (cameraSubsystem.TryUndistortImage(distortedTextures[i], ref undistortedTextures[i]))
        {
            if (VarjoCameraSubsystem.Channel == VarjoStreamChannel.Right)
            {
                skyMaterial.SetTexture("_RightTex", undistortedTextures[i]);
                skyMaterial.SetMatrix("_RightProjection", Camera.main.GetStereoProjectionMatrix(Camera.StereoscopicEye.Right));
            }
            else
            {
                skyMaterial.SetTexture("_LeftTex", undistortedTextures[i]);
                skyMaterial.SetMatrix("_LeftProjection", Camera.main.GetStereoProjectionMatrix(Camera.StereoscopicEye.Left));
            }
        }
    }
}
