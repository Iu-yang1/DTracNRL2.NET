using System.Net;
using System.Net.Sockets;
using System.Text;
using DTrac.Nrl2.Protocol;
using DTrac.Nrl2.Rotator;

// This simulator is deliberately LOOPBACK ONLY: it must never forward to a physical rotator.
using var socket = new UdpClient(new IPEndPoint(IPAddress.Loopback, 19120));
RotatorPosition position = new(180, 30);
Console.WriteLine("DTrac NRL2 offline simulator on 127.0.0.1:19120 (NO hardware control).");
while (true)
{
    var received = await socket.ReceiveAsync();
    if (!IPAddress.IsLoopback(received.RemoteEndPoint.Address) ||
        !Nrl2Frame.TryParse(received.Buffer, out var frame) ||
        frame.Type != Nrl2Frame.Gs232Type || frame.Payload.Length == 0)
        continue;
    string command = Encoding.ASCII.GetString(frame.Payload);
    if (command.StartsWith("W", StringComparison.Ordinal) && command.TrimEnd('\r', '\n').Length == 8)
    {
        string s = command.TrimEnd('\r', '\n');
        if (int.TryParse(s.AsSpan(1, 3), out int az) &&
            int.TryParse(s.AsSpan(5, 3), out int el) &&
            az <= 450 && el <= 180)
            position = new(az, el);
    }
    // Simulator replies to C2 only. STOP deliberately does not confirm motor state.
    if (command != "C2\r\n") continue;
    string reply = FormattableString.Invariant($"AZ={position.Azimuth:000}  EL={position.Elevation:000}\r\n");
    var answer = Nrl2Frame.Encode(frame.TargetId, frame.SourceId, Encoding.ASCII.GetBytes(reply));
    await socket.SendAsync(answer, received.RemoteEndPoint);
}
