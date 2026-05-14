
using UdonSharp;
using UnityEngine;
using VRC.SDK3.Rendering;
using VRC.SDKBase;
using VRC.Udon;

public class CabinCameraStacking : UdonSharpBehaviour
{
    public Camera fakeCamera;
    public Transform StartTransform;
    public Transform EndTransform;

    void Update()
    {
        // VRChat disables cameras, force it on.
        fakeCamera.enabled = true;

        // Classic transform, inverse transform, kind of like how mirrors work
        Vector3 headPosition = Networking.LocalPlayer.GetTrackingData(VRCPlayerApi.TrackingDataType.Head).position;
        Quaternion headRotation = Networking.LocalPlayer.GetTrackingData(VRCPlayerApi.TrackingDataType.Head).rotation;

        Vector3 localPosition = StartTransform.InverseTransformPoint(headPosition);
        Quaternion localRotation = Quaternion.Inverse(StartTransform.rotation) * headRotation;

        fakeCamera.transform.position = EndTransform.TransformPoint(localPosition);
        fakeCamera.transform.rotation = EndTransform.rotation * localRotation;

        // fix IPD
        fakeCamera.transform.localScale = Vector3.one * Networking.LocalPlayer.GetAvatarEyeHeightAsMeters() / 1.75f;

        fakeCamera.fieldOfView = VRCCameraSettings.ScreenCamera.FieldOfView;
        fakeCamera.nearClipPlane = VRCCameraSettings.ScreenCamera.NearClipPlane;
        fakeCamera.farClipPlane = VRCCameraSettings.ScreenCamera.FarClipPlane;
    }
}
