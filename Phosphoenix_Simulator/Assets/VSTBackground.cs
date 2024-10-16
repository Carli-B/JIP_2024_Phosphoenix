using UnityEngine;
using UnityEngine.UI;
using UnityEngine.XR;
using UnityEngine.XR.ARSubsystems;
using UnityEngine.XR.Management;
using Unity.Collections;
using Varjo.XR;


public class VSTBackground : MonoBehaviour
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

        if (cameraSubsystem == null || !cameraSubsystem.IsColorStreamEnabled)
        {
            return;
        }

        if (cameraSubsystem.TryAcquireLatestCpuImage(out XRCpuImage img))
        {
            int convertedDataSize = img.GetConvertedDataSize(img.dimensions, targetFormat);

            using (var array = new NativeArray<byte>(length: convertedDataSize, Allocator.Temp, NativeArrayOptions.UninitializedMemory))
            {
                var conversionParams = new XRCpuImage.ConversionParams(img, targetFormat);

                VarjoCameraSubsystem.Channel = VarjoStreamChannel.Left;
                img.Convert(conversionParams, new NativeSlice<byte>(array));
                SetImage(img.dimensions, array);

                VarjoCameraSubsystem.Channel = VarjoStreamChannel.Right;
                img.Convert(conversionParams, new NativeSlice<byte>(array));
                SetImage(img.dimensions, array);
            }

            img.Dispose();
        }
    }

    void SetImage(Vector2Int dimensions, NativeArray<byte> convertedData)
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

        distortedTextures[i].LoadRawTextureData(convertedData);
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
