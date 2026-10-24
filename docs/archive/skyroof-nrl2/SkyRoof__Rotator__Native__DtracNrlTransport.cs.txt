using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Threading;
namespace SkyRoof.NativeRotator;
internal sealed class DtracNrlTransport:IRotatorTransport
{
  private readonly RotatorSettings options;
  private UdpClient? socket;
  internal DtracNrlTransport(RotatorSettings options)=>this.options=options;
  public bool Connected=>socket!=null;
  public void Open()
  {
    if(options.NativeTransport!=RotatorTransportKind.Udp)throw new InvalidOperationException("DTrac requires UDP");
    if(string.IsNullOrWhiteSpace(options.DtracNrlServerHost))throw new ArgumentException("Set NRL2 server host");
    DtracNrl2Codec.ParseIdentifier(options.DtracNrlRotorId);
    DtracNrl2Codec.ParseIdentifier(options.DtracNrlAppSid);
    if(options.DtracNrlServerPort==0)throw new ArgumentException("Invalid NRL2 port");
    var u=new UdpClient(AddressFamily.InterNetwork);
    try {
      u.Connect(options.DtracNrlServerHost,options.DtracNrlServerPort);
      u.Client.ReceiveTimeout=Math.Clamp(options.NativeReadTimeout,100,30000);
      socket=u;
    }catch {u.Dispose();throw;}
  }
  public void Write(ReadOnlySpan<byte> command)
  {
    var u=socket??throw new IOException("DTrac socket closed");
    byte[] packet=DtracNrl2Codec.BuildCommand(options.DtracNrlAppSid,options.DtracNrlRotorId,command);
    u.Send(packet,packet.Length);
    if(command.SequenceEqual("S\r\n"u8)){Thread.Sleep(20);u.Send(packet,packet.Length);}
  }
  public int Read(byte[] buffer)
  {
    var u=socket??throw new IOException("DTrac socket closed");
    try {
      IPEndPoint remote=new(IPAddress.Any,0);
      byte[] frame=u.Receive(ref remote);
      if(!DtracNrl2Codec.TryDecodeReply(frame,options.DtracNrlRotorId,options.DtracNrlAppSid,out var bytes))return 0;
      if(bytes.Length>buffer.Length)throw new InvalidDataException("Reply buffer overflow");
      bytes.CopyTo(buffer,0);return bytes.Length;
    }catch(SocketException e) when(e.SocketErrorCode==SocketError.TimedOut){return 0;}
  }
  public void Abort()=>Interlocked.Exchange(ref socket,null)?.Dispose();
  public void Dispose()=>Abort();
}