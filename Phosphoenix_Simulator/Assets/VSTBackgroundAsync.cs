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


public class VSTBackgroundAsync : MonoBehaviour
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
    private int CPUImageSizeBytesFor1Channel;

    private NativeArray<byte> CPUImageRawData;
    private Vector2Int CPUImageDimensions;


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

        if (CPUImageRawData.IsCreated)
        {
            CPUImageRawData.Dispose();
        }
    }

    void Update()
    {
        if (cameraSubsystem == null || !cameraSubsystem.IsColorStreamEnabled) return;

        if (!cameraSubsystem.TryAcquireLatestCpuImage(out XRCpuImage image)) return;


        if (CPUImageRawData.IsCreated)
        {
            VarjoCameraSubsystem.Channel = VarjoStreamChannel.Left;
            SetImage(CPUImageDimensions, CPUImageRawData, 0);

            VarjoCameraSubsystem.Channel = VarjoStreamChannel.Right;
            SetImage(CPUImageDimensions, CPUImageRawData, 1);
        }

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

        var conversionParams = new XRCpuImage.ConversionParams(image, targetFormat);

        image.ConvertAsync(conversionParams, (AsyncConversionStatus status, ConversionParams @params, NativeArray<byte> array) => {

            if (CPUImageRawData.IsCreated && CPUImageRawData.Length != array.Length)
            {
                CPUImageRawData.Dispose();
            }
            if (!CPUImageRawData.IsCreated)
            {
                CPUImageRawData = new NativeArray<byte>(array.Length, Allocator.Persistent);
            }

            array.CopyTo(CPUImageRawData);

            CPUImageDimensions = @params.outputDimensions;
            CPUImageSizeBytesFor1Channel = image.GetConvertedDataSize(@params.outputDimensions, targetFormat);

            image.Dispose();
        });
    }

    public void SetImage(Vector2Int dimensions, NativeArray<byte> array, int offset)
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
            byte* convertedData = ((byte*)NativeArrayUnsafeUtility.GetUnsafeBufferPointerWithoutChecks(array)) + CPUImageSizeBytesFor1Channel * offset;
            distortedTextures[i].LoadRawTextureData((IntPtr)convertedData, CPUImageSizeBytesFor1Channel);
        }

        distortedTextures[i].Apply();

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
