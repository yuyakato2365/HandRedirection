using System;
using UnityEngine;

[Serializable]
public class ScanPayload
{
    [Serializable]
    public class Prompt
    {
        public string type; // "point"
        public float u;     // pixel coordinate in captured image
        public float v;
    }

    [Serializable]
    public class SphereWorld
    {
        public float cx, cy, cz;
        public float r; // meters
    }

    [Serializable]
    public class Pose
    {
        public float px, py, pz;
        public float qx, qy, qz, qw; // quaternion
    }

    [Serializable]
    public class CameraInfo
    {
        public float fx, fy, cx, cy;
        public Pose T_wc;  // camera pose in world at capture time
    }

    public Prompt prompt;
    public SphereWorld sphere_world;
    public CameraInfo camera;
}
