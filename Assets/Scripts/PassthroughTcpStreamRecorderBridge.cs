using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Threading;
using Unity.Collections;
using UnityEngine;

public sealed class PassthroughTcpStreamRecorderBridge : MonoBehaviour
{
    [Header("Command UDP")]
    public int listenPort = 9201;
    public bool forceIPv4 = true;

    [Header("Status UDP")]
    public bool sendStatus = true;
    public int statusPort = 9202;
    public string statusHostOverride = "";

    [Header("Passthrough Camera")]
    [Tooltip("Assign the Meta PassthroughCameraAccess component, normally the right-eye camera.")]
    public MonoBehaviour passthroughCameraAccess;
    public bool autoFindPassthroughCameraAccess = true;
    public bool autoCreatePassthroughCameraAccess = true;
    public bool preferRightCamera = true;
    public Vector2Int requestedResolution = new Vector2Int(1280, 960);

    [Header("Stream Defaults")]
    [Range(1, 30)] public int framesPerSecond = 15;
    [Range(10, 100)] public int jpegQuality = 75;
    [Tooltip("Longest output side in pixels. 0 keeps the camera resolution.")]
    public int maxLongSidePixels = 1280;

    [Header("Debug")]
    public bool logCommands = true;

    private UdpClient udp;
    private Thread receiveThread;
    private volatile bool running;
    private readonly object queueLock = new object();
    private readonly Queue<string> pendingCommands = new Queue<string>();
    private IPEndPoint lastSender;

    private Coroutine streamCoroutine;
    private TcpClient tcpClient;
    private NetworkStream tcpStream;
    private Texture2D sourceTexture;
    private Texture2D outputTexture;
    private RenderTexture resizeTexture;

    private void Awake()
    {
        ResolvePassthroughCameraAccess();
    }

    private void OnEnable()
    {
        StartReceiver();
    }

    private void OnDisable()
    {
        StopStreaming("component_disabled");
        StopReceiver();
    }

    private void Update()
    {
        while (TryDequeueCommand(out string command))
            HandleCommand(command);
    }

    private void ResolvePassthroughCameraAccess()
    {
        if (passthroughCameraAccess != null || !autoFindPassthroughCameraAccess)
            return;

        MonoBehaviour[] behaviours = FindObjectsByType<MonoBehaviour>(FindObjectsSortMode.None);
        foreach (MonoBehaviour behaviour in behaviours)
        {
            if (behaviour == null)
                continue;

            string typeName = behaviour.GetType().Name;
            if (typeName.IndexOf("PassthroughCameraAccess", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                passthroughCameraAccess = behaviour;
                break;
            }
        }

        if (passthroughCameraAccess == null && autoCreatePassthroughCameraAccess)
            passthroughCameraAccess = CreatePassthroughCameraAccess();
    }

    private MonoBehaviour CreatePassthroughCameraAccess()
    {
        Type pcaType = FindTypeByName("Meta.XR.PassthroughCameraAccess") ?? FindTypeByName("PassthroughCameraAccess");
        if (pcaType == null || !typeof(MonoBehaviour).IsAssignableFrom(pcaType))
        {
            Debug.LogWarning("[PassthroughTcpStreamRecorderBridge] PassthroughCameraAccess type was not found. Is Meta MR Utility Kit installed?");
            return null;
        }

        GameObject cameraObject = new GameObject("Auto PassthroughCameraAccess");
        MonoBehaviour component = cameraObject.AddComponent(pcaType) as MonoBehaviour;
        ConfigurePassthroughCameraAccess(component);
        Debug.Log("[PassthroughTcpStreamRecorderBridge] Created Auto PassthroughCameraAccess.");
        return component;
    }

    private void ConfigurePassthroughCameraAccess(MonoBehaviour component)
    {
        if (component == null)
            return;

        Type type = component.GetType();
        FieldInfo cameraPosition = type.GetField("CameraPosition", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        if (cameraPosition != null && cameraPosition.FieldType.IsEnum)
        {
            string enumName = preferRightCamera ? "Right" : "Left";
            object value = Enum.Parse(cameraPosition.FieldType, enumName);
            cameraPosition.SetValue(component, value);
        }

        FieldInfo requested = type.GetField("RequestedResolution", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        if (requested != null && requested.FieldType == typeof(Vector2Int))
            requested.SetValue(component, requestedResolution);
    }

    private static Type FindTypeByName(string typeName)
    {
        foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
        {
            Type exact = assembly.GetType(typeName);
            if (exact != null)
                return exact;

            foreach (Type type in GetLoadableTypes(assembly))
            {
                if (type.Name == typeName)
                    return type;
            }
        }

        return null;
    }

    private static Type[] GetLoadableTypes(Assembly assembly)
    {
        try
        {
            return assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException e)
        {
            List<Type> types = new List<Type>();
            foreach (Type type in e.Types)
            {
                if (type != null)
                    types.Add(type);
            }

            return types.ToArray();
        }
    }

    private void StartReceiver()
    {
        try
        {
            udp = forceIPv4 ? new UdpClient(AddressFamily.InterNetwork) : new UdpClient(AddressFamily.Unspecified);
            udp.Client.Bind(new IPEndPoint(IPAddress.Any, listenPort));
            running = true;
            receiveThread = new Thread(ReceiveLoop)
            {
                IsBackground = true,
                Name = "PassthroughTcpStreamRecorderBridge"
            };
            receiveThread.Start();
            SendStatus("PASSTHROUGH_BRIDGE_READY");
        }
        catch (Exception e)
        {
            Debug.LogError($"[PassthroughTcpStreamRecorderBridge] UDP init failed: {e.Message}");
            running = false;
            StopReceiver();
        }
    }

    private void StopReceiver()
    {
        running = false;

        try { udp?.Close(); } catch { }
        udp = null;

        try
        {
            if (receiveThread != null && receiveThread.IsAlive)
                receiveThread.Join(200);
        }
        catch { }

        receiveThread = null;
    }

    private void ReceiveLoop()
    {
        while (running && udp != null)
        {
            try
            {
                IPEndPoint sender = new IPEndPoint(IPAddress.Any, 0);
                byte[] data = udp.Receive(ref sender);
                string command = Encoding.UTF8.GetString(data).Trim();

                lock (queueLock)
                {
                    lastSender = sender;
                    pendingCommands.Enqueue(command);
                }
            }
            catch (SocketException)
            {
                if (!running)
                    break;
            }
            catch (ObjectDisposedException)
            {
                break;
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[PassthroughTcpStreamRecorderBridge] Receive error: {e.Message}");
            }
        }
    }

    private bool TryDequeueCommand(out string command)
    {
        lock (queueLock)
        {
            if (pendingCommands.Count == 0)
            {
                command = null;
                return false;
            }

            command = pendingCommands.Dequeue();
            return true;
        }
    }

    private void HandleCommand(string command)
    {
        if (string.IsNullOrWhiteSpace(command))
            return;

        if (logCommands)
            Debug.Log($"[PassthroughTcpStreamRecorderBridge] Command: {command}");

        string[] tokens = command.Split(new[] { ' ', '\t', '\r', '\n', ';' }, StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length == 0)
            return;

        string verb = tokens[0].Trim().ToUpperInvariant();
        Dictionary<string, string> args = ParseArgs(tokens);

        switch (verb)
        {
            case "START_PASSTHROUGH_STREAM":
            case "START_RECORDING":
                StartStreamingFromArgs(args);
                break;
            case "STOP_PASSTHROUGH_STREAM":
            case "STOP_RECORDING":
                StopStreaming("pc_stop");
                break;
            case "PING":
                SendStatus("PASSTHROUGH_BRIDGE_PONG");
                break;
            default:
                SendStatus($"ERROR unknown_command {verb}");
                break;
        }
    }

    private static Dictionary<string, string> ParseArgs(string[] tokens)
    {
        Dictionary<string, string> args = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (int i = 1; i < tokens.Length; i++)
        {
            string token = tokens[i];
            int equals = token.IndexOf('=');
            if (equals <= 0)
                continue;

            args[token.Substring(0, equals).Trim()] = token.Substring(equals + 1).Trim();
        }

        return args;
    }

    private void StartStreamingFromArgs(Dictionary<string, string> args)
    {
        ResolvePassthroughCameraAccess();
        if (passthroughCameraAccess == null)
        {
            SendStatus("ERROR passthrough_camera_not_assigned");
            return;
        }

        string host = GetArg(args, "host", "");
        int port = GetArg(args, "port", 0);
        if (string.IsNullOrWhiteSpace(host) || port <= 0)
        {
            SendStatus("ERROR missing_host_or_port");
            return;
        }

        int fps = Mathf.Clamp(GetArg(args, "fps", framesPerSecond), 1, 30);
        int quality = Mathf.Clamp(GetArg(args, "quality", jpegQuality), 10, 100);
        int maxLongSide = Mathf.Max(0, GetArg(args, "maxLongSide", maxLongSidePixels));

        StopStreaming("restart");
        streamCoroutine = StartCoroutine(StreamCoroutine(host, port, fps, quality, maxLongSide));
    }

    private static string GetArg(Dictionary<string, string> args, string key, string fallback)
    {
        return args.TryGetValue(key, out string value) ? value : fallback;
    }

    private static int GetArg(Dictionary<string, string> args, string key, int fallback)
    {
        if (!args.TryGetValue(key, out string value))
            return fallback;

        return int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed) ? parsed : fallback;
    }

    private IEnumerator StreamCoroutine(string host, int port, int fps, int quality, int maxLongSide)
    {
        SendStatus($"PASSTHROUGH_STREAM_CONNECTING {host}:{port}");
        SendStatus(BuildPassthroughCameraDiagnostics("PASSTHROUGH_CAMERA_DIAGNOSTICS"));

        float start = Time.realtimeSinceStartup;
        while (!GetBoolProp(passthroughCameraAccess, "IsPlaying", false))
        {
            if (Time.realtimeSinceStartup - start > 5f)
            {
                SendStatus(BuildPassthroughCameraDiagnostics("ERROR passthrough_camera_not_playing"));
                yield break;
            }

            yield return null;
        }

        if (!OpenTcp(host, port))
            yield break;

        SendStatus("PASSTHROUGH_STREAM_STARTED");

        float frameInterval = 1f / Mathf.Max(1, fps);
        while (tcpClient != null && tcpClient.Connected)
        {
            float frameStart = Time.realtimeSinceStartup;
            if (!TryCaptureJpeg(maxLongSide, quality, out byte[] jpeg, out string error))
            {
                SendStatus($"ERROR capture_failed {error}");
                yield return new WaitForSeconds(0.25f);
                continue;
            }

            try
            {
                byte[] length = BitConverter.GetBytes(jpeg.Length);
                tcpStream.Write(length, 0, length.Length);
                tcpStream.Write(jpeg, 0, jpeg.Length);
                tcpStream.Flush();
            }
            catch (Exception e)
            {
                SendStatus($"PASSTHROUGH_STREAM_WRITE_FAILED {e.Message}");
                break;
            }

            float elapsed = Time.realtimeSinceStartup - frameStart;
            if (elapsed < frameInterval)
                yield return new WaitForSeconds(frameInterval - elapsed);
            else
                yield return null;
        }

        StopStreaming("stream_ended");
    }

    private bool OpenTcp(string host, int port)
    {
        try
        {
            tcpClient = new TcpClient();
            tcpClient.NoDelay = true;
            tcpClient.Connect(host, port);
            tcpStream = tcpClient.GetStream();
            return true;
        }
        catch (Exception e)
        {
            SendStatus($"ERROR tcp_connect_failed {e.Message}");
            CloseTcp();
            return false;
        }
    }

    private bool TryCaptureJpeg(int maxLongSide, int quality, out byte[] jpeg, out string error)
    {
        jpeg = null;
        error = null;

        Vector2Int resolution = GetVector2IntProp(passthroughCameraAccess, "CurrentResolution", Vector2Int.zero);
        if (resolution.x <= 0 || resolution.y <= 0)
        {
            error = "invalid_resolution";
            return false;
        }

        object colorsObj = InvokeMethod(passthroughCameraAccess, "GetColors");
        if (colorsObj == null)
        {
            error = "get_colors_missing";
            return false;
        }

        NativeArray<Color32> colors;
        try
        {
            colors = (NativeArray<Color32>)colorsObj;
        }
        catch
        {
            error = "get_colors_type";
            return false;
        }

        if (!colors.IsCreated || colors.Length != resolution.x * resolution.y)
        {
            error = "colors_mismatch";
            return false;
        }

        EnsureSourceTexture(resolution.x, resolution.y);
        sourceTexture.SetPixels32(colors.ToArray());
        sourceTexture.Apply(false, false);

        Texture2D encodeTexture = sourceTexture;
        if (maxLongSide > 0 && Mathf.Max(resolution.x, resolution.y) > maxLongSide)
            encodeTexture = ResizeForOutput(sourceTexture, maxLongSide);

        jpeg = encodeTexture.EncodeToJPG(quality);
        return jpeg != null && jpeg.Length > 0;
    }

    private string BuildPassthroughCameraDiagnostics(string prefix)
    {
        if (passthroughCameraAccess == null)
            return $"{prefix} assigned=false";

        Type type = passthroughCameraAccess.GetType();
        bool isPlaying = GetBoolProp(passthroughCameraAccess, "IsPlaying", false);
        Vector2Int resolution = GetVector2IntProp(passthroughCameraAccess, "CurrentResolution", Vector2Int.zero);
        string isSupported = GetStaticBoolProp(type, "IsSupported", out bool supported) ? supported.ToString() : "unknown";

        return $"{prefix} assigned=true type={type.FullName} platform={Application.platform} isEditor={Application.isEditor} isSupported={isSupported} isPlaying={isPlaying} resolution={resolution.x}x{resolution.y}";
    }

    private void EnsureSourceTexture(int width, int height)
    {
        if (sourceTexture != null && sourceTexture.width == width && sourceTexture.height == height)
            return;

        if (sourceTexture != null)
            Destroy(sourceTexture);

        sourceTexture = new Texture2D(width, height, TextureFormat.RGBA32, false, false);
    }

    private Texture2D ResizeForOutput(Texture2D source, int maxLongSide)
    {
        float scale = maxLongSide / (float)Mathf.Max(source.width, source.height);
        int width = Mathf.Max(2, Mathf.RoundToInt(source.width * scale));
        int height = Mathf.Max(2, Mathf.RoundToInt(source.height * scale));

        if (resizeTexture == null || resizeTexture.width != width || resizeTexture.height != height)
        {
            if (resizeTexture != null)
                resizeTexture.Release();

            resizeTexture = new RenderTexture(width, height, 0, RenderTextureFormat.ARGB32);
            resizeTexture.Create();
        }

        if (outputTexture == null || outputTexture.width != width || outputTexture.height != height)
        {
            if (outputTexture != null)
                Destroy(outputTexture);

            outputTexture = new Texture2D(width, height, TextureFormat.RGBA32, false, false);
        }

        RenderTexture previous = RenderTexture.active;
        Graphics.Blit(source, resizeTexture);
        RenderTexture.active = resizeTexture;
        outputTexture.ReadPixels(new Rect(0, 0, width, height), 0, 0, false);
        outputTexture.Apply(false, false);
        RenderTexture.active = previous;
        return outputTexture;
    }

    private void StopStreaming(string reason)
    {
        if (streamCoroutine != null)
        {
            StopCoroutine(streamCoroutine);
            streamCoroutine = null;
        }

        CloseTcp();
        SendStatus($"PASSTHROUGH_STREAM_STOPPED {reason}");
    }

    private void CloseTcp()
    {
        try { tcpStream?.Close(); } catch { }
        try { tcpClient?.Close(); } catch { }
        tcpStream = null;
        tcpClient = null;
    }

    private void OnDestroy()
    {
        if (sourceTexture != null)
            Destroy(sourceTexture);
        if (outputTexture != null)
            Destroy(outputTexture);
        if (resizeTexture != null)
            resizeTexture.Release();
    }

    private void SendStatus(string message)
    {
        if (!sendStatus)
            return;

        try
        {
            string host = statusHostOverride;
            if (string.IsNullOrWhiteSpace(host))
            {
                lock (queueLock)
                {
                    host = lastSender != null ? lastSender.Address.ToString() : null;
                }
            }

            if (string.IsNullOrWhiteSpace(host))
                return;

            using (UdpClient statusClient = new UdpClient())
            {
                byte[] data = Encoding.UTF8.GetBytes(message);
                statusClient.Send(data, data.Length, host, statusPort);
            }
        }
        catch (Exception e)
        {
            Debug.LogWarning($"[PassthroughTcpStreamRecorderBridge] Status send failed: {e.Message}");
        }
    }

    private static object InvokeMethod(object obj, string name)
    {
        MethodInfo mi = obj.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        return mi?.Invoke(obj, null);
    }

    private static bool GetBoolProp(object obj, string name, bool fallback)
    {
        PropertyInfo pi = obj.GetType().GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        if (pi == null)
            return fallback;

        object value = pi.GetValue(obj);
        return value is bool b ? b : fallback;
    }

    private static Vector2Int GetVector2IntProp(object obj, string name, Vector2Int fallback)
    {
        PropertyInfo pi = obj.GetType().GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        if (pi == null)
            return fallback;

        object value = pi.GetValue(obj);
        return value is Vector2Int v ? v : fallback;
    }

    private static bool GetStaticBoolProp(Type type, string name, out bool value)
    {
        value = false;
        try
        {
            PropertyInfo pi = type.GetProperty(name, BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
            if (pi == null)
                return false;

            object raw = pi.GetValue(null);
            if (raw is bool b)
            {
                value = b;
                return true;
            }
        }
        catch
        {
            return false;
        }

        return false;
    }
}
