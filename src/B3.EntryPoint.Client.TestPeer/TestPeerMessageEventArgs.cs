using System;

namespace B3.EntryPoint.Client.TestPeer;

/// <summary>
/// Event raised by <see cref="InProcessFixpTestPeer"/> for every inbound
/// frame the peer reads from a connected client (after SOFH framing,
/// including session-layer messages such as Negotiate/Establish/Terminate
/// and application messages such as NewOrderSingle).
/// </summary>
/// <remarks>
/// The payload buffer is the SBE message body only — SOFH header and SBE
/// MessageHeader are stripped. The buffer is owned by the peer and only
/// valid for the duration of the event handler; copy if you need to retain
/// it. The handler runs on the peer's connection task — do not block.
/// </remarks>
public sealed class TestPeerMessageEventArgs : EventArgs
{
    public TestPeerMessageEventArgs(ushort templateId, ReadOnlyMemory<byte> payload)
    {
        TemplateId = templateId;
        Payload = payload;
    }

    /// <summary>SBE template id (e.g. <c>NegotiateData.MESSAGE_ID</c>).</summary>
    public ushort TemplateId { get; }

    /// <summary>SBE message body (SOFH and SBE MessageHeader stripped).</summary>
    public ReadOnlyMemory<byte> Payload { get; }
}
