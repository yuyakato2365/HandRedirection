using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Unity.WebRTC;
using UnityEngine;
using UnityEngine.UI;

public class IpadWebRtcReceiver : MonoBehaviour
{
    [Header("Signaling (WebSocket relay)")]
    [SerializeField] private string signalingUrl = "ws://localhost:8080";

    [Header("Status UI (optional)")]
    [SerializeField] private Text statusText;

    [Header("Video output (world)")]
    [Tooltip("映像を貼る対象（Quad/Planeなど）のRendererを指定")]
    [SerializeField] private Renderer targetRenderer;

    [Tooltip("iPad映像が上下反転している場合はON")]
    [SerializeField] private bool flipY = false;

    [Header("Signaling role tags (optional but recommended)")]
    [SerializeField] private string myRole = "unity";   // Unityが送るメッセージに付けるrole
    [SerializeField] private string peerRole = "ipad";  // iPadが送ってくる想定role（空なら無視しない）

    private ClientWebSocket _ws;
    private CancellationTokenSource _cts;

    private RTCPeerConnection _pc;
    private MediaStream _receiveStream;

    private bool _webrtcUpdateStarted;

    // WS受信→メインスレッドへ渡す
    private readonly ConcurrentQueue<string> _inbox = new ConcurrentQueue<string>();

    // Offer/RemoteDescription前に来たICEを貯める
    private readonly List<IceMessage> _pendingRemoteIce = new List<IceMessage>();
    private bool _remoteDescriptionSet = false;

    // 受信動画トラック参照保持（GC対策）
    private VideoStreamTrack _remoteVideo;

    // VideoReceivedで来たTextureをメインスレッドで反映するための箱
    private readonly ConcurrentQueue<Texture> _videoFrames = new ConcurrentQueue<Texture>();

    // 受信テクスチャが変わってもMaterial参照を持ち替えないようにキャッシュ（任意）
    private Material _matCache;

    [Serializable]
    private class SdpMessage
    {
        public string type;  // "offer" / "answer"
        public string sdp;
        public string role;  // "ipad" / "unity" など（任意）
    }

    [Serializable]
    private class IceMessage
    {
        public string type;  // "ice" or "candidate" (両対応)
        public string candidate;
        public string sdpMid;
        public int sdpMLineIndex;
        public string role;  // 任意
    }

    // JSONを一回で拾うための“スーパーセット”構造体
    // （type無しoffer、type無しiceなどの揺れにも対応）
    [Serializable]
    private class SignalEnvelope
    {
        public string type;
        public string sdp;
        public string candidate;
        public string sdpMid;
        public int sdpMLineIndex;
        public string role;
    }

    private void Start()
    {
        _receiveStream = new MediaStream();
        _receiveStream.OnAddTrack = e =>
        {
            Debug.Log($"[Receiver] OnAddTrack: {e.Track?.Kind} id={e.Track?.Id}");
        };

        // WebRTC.Update() は毎フレーム回す必要がある
        if (!_webrtcUpdateStarted)
        {
            StartCoroutine(WebRTC.Update());
            _webrtcUpdateStarted = true;
        }

        // RendererのMaterialをキャッシュ（任意：毎回material触るとインスタンスが増えるのを避けたい）
        if (targetRenderer != null)
        {
            _matCache = targetRenderer.material;
        }

        Log("Ready. Waiting offer...");
        _ = ConnectAndListen();
    }

    private void Update()
    {
        // ① シグナリング処理（メインスレッド）
        while (_inbox.TryDequeue(out var json))
        {
            HandleSignaling(json);
        }

        // ② 受信フレーム反映（メインスレッド）
        // 最新だけ貼れば十分なので全て捨てて最後の1枚を採用
        Texture last = null;
        while (_videoFrames.TryDequeue(out var tex))
        {
            last = tex;
        }

        if (last != null && targetRenderer != null)
        {
            if (_matCache == null)
                _matCache = targetRenderer.material;

            _matCache.mainTexture = last;

            if (flipY)
            {
                _matCache.mainTextureScale = new Vector2(1f, -1f);
                _matCache.mainTextureOffset = new Vector2(0f, 1f);
            }
            else
            {
                _matCache.mainTextureScale = Vector2.one;
                _matCache.mainTextureOffset = Vector2.zero;
            }
        }
    }

    private RTCConfiguration CreateConfig()
    {
        RTCConfiguration config = default;
        config.iceServers = new[]
        {
            new RTCIceServer { urls = new[] { "stun:stun.l.google.com:19302" } }
        };
        return config;
    }

    private void CreatePeerIfNeeded()
    {
        if (_pc != null) return;

        var config = CreateConfig();
        _pc = new RTCPeerConnection(ref config);

        // Unity→相手へ ICE 送信
        _pc.OnIceCandidate = candidate =>
        {
            if (candidate == null) return;

            var msg = new IceMessage
            {
                type = "ice",
                candidate = candidate.Candidate,
                sdpMid = candidate.SdpMid,
                sdpMLineIndex = candidate.SdpMLineIndex ?? 0,
                role = myRole
            };
            _ = SendJson(msg);
        };

        // ★ここが映像受信の本体
        _pc.OnTrack = e =>
        {
            Debug.Log($"[Receiver] OnTrack: {e.Track?.Kind} id={e.Track?.Id}");

            if (e.Track is VideoStreamTrack vt)
            {
                _remoteVideo = vt;

                vt.OnVideoReceived += tex =>
                {
                    // 注意：ここはワーカースレッドで来ることがあるので、キューへ
                    // Debug.Logは重いので必要なら間引く
                    _videoFrames.Enqueue(tex);
                };
            }

            _receiveStream.AddTrack(e.Track);
        };

        _pc.OnIceConnectionChange = state => Log($"ICE: {state}");
        _pc.OnConnectionStateChange = state => Log($"PC: {state}");
    }

    // Unity.WebRTC AsyncOperation は await できないので IsDone をポーリング
    private static async Task WaitOp(RTCSetSessionDescriptionAsyncOperation op)
    {
        while (!op.IsDone) await Task.Yield();
        if (op.IsError) throw new Exception(op.Error.message);
    }

    private static async Task WaitOp(RTCSessionDescriptionAsyncOperation op)
    {
        while (!op.IsDone) await Task.Yield();
        if (op.IsError) throw new Exception(op.Error.message);
    }

    private async Task ConnectAndListen()
    {
        try
        {
            _cts = new CancellationTokenSource();
            _ws = new ClientWebSocket();
            await _ws.ConnectAsync(new Uri(signalingUrl), _cts.Token);
            Log($"WS connected: {signalingUrl}");

            // 受信ループ：ここでは処理せずキューに積むだけ
            var buf = new byte[1 << 16];
            while (_ws.State == WebSocketState.Open)
            {
                var sb = new StringBuilder();
                WebSocketReceiveResult res;

                do
                {
                    res = await _ws.ReceiveAsync(new ArraySegment<byte>(buf), _cts.Token);
                    if (res.MessageType == WebSocketMessageType.Close) break;

                    sb.Append(Encoding.UTF8.GetString(buf, 0, res.Count));
                }
                while (!res.EndOfMessage);

                if (res.MessageType == WebSocketMessageType.Close) break;

                _inbox.Enqueue(sb.ToString());
            }
        }
        catch (Exception ex)
        {
            Debug.LogException(ex);
            Log($"WS error: {ex.Message}");
        }
    }

    private void HandleSignaling(string json)
    {
        SignalEnvelope env = null;
        try
        {
            env = JsonUtility.FromJson<SignalEnvelope>(json);
        }
        catch
        {
            Debug.LogWarning("[Receiver] JSON parse failed: " + json);
            return;
        }

        // ① 自分が送ったメッセージを誤処理しない（relayは全員に投げ返すため）
        if (!string.IsNullOrEmpty(env.role) && env.role == myRole)
        {
            return;
        }

        // ② roleフィルタ（任意）：peerRoleが設定されていて、相手roleが明示されている場合だけ弾く
        //    ※offerにroleが無いケースがあるので「roleが無いなら通す」
        if (!string.IsNullOrEmpty(peerRole) && !string.IsNullOrEmpty(env.role) && env.role != peerRole)
        {
            return;
        }

        // ③ typeの揺れに対応する
        var t = env.type ?? "";

        bool hasSdp = !string.IsNullOrEmpty(env.sdp);
        bool hasCand = !string.IsNullOrEmpty(env.candidate);

        if (t == "offer" || (string.IsNullOrEmpty(t) && hasSdp && !hasCand))
        {
            Debug.Log("[Receiver] Got OFFER (type may be missing)");
            _ = OnOffer(env.sdp);
            return;
        }

        if (t == "ice" || t == "candidate" || (string.IsNullOrEmpty(t) && hasCand))
        {
            var iceMsg = new IceMessage
            {
                type = "ice",
                candidate = env.candidate,
                sdpMid = env.sdpMid,
                sdpMLineIndex = env.sdpMLineIndex,
                role = env.role
            };
            OnRemoteIce(iceMsg);
            return;
        }

        if (t == "answer")
        {
            Debug.Log("[Receiver] Got ANSWER msg (unexpected for receiver, but handled)");
            _ = OnAnswer(env.sdp);
            return;
        }

        Debug.Log("[Receiver] Unknown msg: " + json);
    }

    private async Task OnOffer(string sdp)
    {
        try
        {
            CreatePeerIfNeeded();
            _remoteDescriptionSet = false;

            Log("Got OFFER. SetRemoteDescription...");

            var offer = new RTCSessionDescription { type = RTCSdpType.Offer, sdp = sdp };
            var op1 = _pc.SetRemoteDescription(ref offer);
            await WaitOp(op1);

            _remoteDescriptionSet = true;
            FlushPendingIce();

            Log("CreateAnswer...");
            var op2 = _pc.CreateAnswer();
            await WaitOp(op2);

            var answer = op2.Desc;

            Log("SetLocalDescription(answer)...");
            var op3 = _pc.SetLocalDescription(ref answer);
            await WaitOp(op3);

            await SendJson(new SdpMessage { type = "answer", sdp = answer.sdp, role = myRole });
            Log("Sent ANSWER.");
        }
        catch (Exception ex)
        {
            Log($"Offer handling error: {ex.Message}");
        }
    }

    private async Task OnAnswer(string sdp)
    {
        if (_pc == null) return;

        try
        {
            var ans = new RTCSessionDescription { type = RTCSdpType.Answer, sdp = sdp };
            var op = _pc.SetRemoteDescription(ref ans);
            await WaitOp(op);
            Log("Set remote ANSWER.");
        }
        catch (Exception ex)
        {
            Log($"Answer handling error: {ex.Message}");
        }
    }

    private void OnRemoteIce(IceMessage ice)
    {
        if (_pc == null || !_remoteDescriptionSet)
        {
            _pendingRemoteIce.Add(ice);
            return;
        }

        AddIceToPc(ice);
    }

    private void FlushPendingIce()
    {
        if (_pc == null || !_remoteDescriptionSet) return;

        if (_pendingRemoteIce.Count > 0)
        {
            Debug.Log($"[Receiver] Flushing ICE: {_pendingRemoteIce.Count}");
            foreach (var ice in _pendingRemoteIce)
                AddIceToPc(ice);
            _pendingRemoteIce.Clear();
        }
    }

    private void AddIceToPc(IceMessage ice)
    {
        try
        {
            var init = new RTCIceCandidateInit
            {
                candidate = ice.candidate,
                sdpMid = ice.sdpMid,
                sdpMLineIndex = ice.sdpMLineIndex
            };
            _pc.AddIceCandidate(new RTCIceCandidate(init));
        }
        catch (Exception ex)
        {
            Debug.Log($"[Receiver] AddIceCandidate error: {ex.Message}");
        }
    }

    private async Task SendJson(object obj)
    {
        if (_ws == null || _ws.State != WebSocketState.Open) return;

        var json = JsonUtility.ToJson(obj);
        var bytes = Encoding.UTF8.GetBytes(json);

        await _ws.SendAsync(
            new ArraySegment<byte>(bytes),
            WebSocketMessageType.Text,
            true,
            _cts.Token
        );
    }

    private void Log(string msg)
    {
        Debug.Log(msg);
        if (statusText != null) statusText.text = msg;
    }

    private async void OnDestroy()
    {
        try
        {
            if (_remoteVideo != null)
            {
                _remoteVideo.Dispose();
                _remoteVideo = null;
            }

            _pc?.Close();
            _pc?.Dispose();
            _pc = null;

            _receiveStream?.Dispose();
            _receiveStream = null;

            if (_ws != null && (_ws.State == WebSocketState.Open || _ws.State == WebSocketState.CloseReceived))
                await _ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", CancellationToken.None);
        }
        catch
        {
            // ignore
        }

        _cts?.Cancel();
        _ws?.Dispose();
        _cts?.Dispose();
    }
}
