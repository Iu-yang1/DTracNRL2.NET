using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
namespace SkyRoof.NativeRotator;
public sealed record DtracDiscoveredRotor(string Address,int TcpPort,string RotorId);
public static class DtracDiscovery
{
  public const int Port=60059;
  public static bool TryParsePong(ReadOnlySpan<byte> frame,IPAddress remote,out DtracDiscoveredRotor? result)
  {
    result=null;
    if(frame.Length==0 || frame.Length>512 || remote.AddressFamily!=AddressFamily.InterNetwork)return false;
    string[] parts=Encoding.ASCII.GetString(frame).Trim('\0','\r','\n',' ').Split('&');
    if(parts.Length<2 || !parts[0].Equals("cmd=pong",StringComparison.OrdinalIgnoreCase))return false;
    var d=new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase);
    foreach(string part in parts.Skip(1)){int i=part.IndexOf('=');if(i>0)d[part[..i]]=part[(i+1)..];}
    if(!d.TryGetValue("type",out var type) || !(type=="1" || type.Equals("RC-3040S",StringComparison.OrdinalIgnoreCase)))return false;
    if(!d.TryGetValue("id",out var rid) && !d.TryGetValue("rotorid",out rid))return false;
    byte[] id;
    try{id=DtracNrl2Codec.ParseIdentifier(rid);}
    catch(ArgumentException){return false;}
    int port=60020;
    if(d.TryGetValue("port",out var pp) && (!int.TryParse(pp,NumberStyles.None,CultureInfo.InvariantCulture,out port) || port is <1 or >65535))return false;
    if(d.TryGetValue("ip",out var ip) && (!IPAddress.TryParse(ip,out var address) || !address.Equals(remote)))return false;
    result=new(remote.ToString(),port,Convert.ToHexString(id));
    return true;
  }
  public static async Task<IReadOnlyList<DtracDiscoveredRotor>> DiscoverAsync(TimeSpan timeout,CancellationToken token=default)
  {
    using var socket=new UdpClient(AddressFamily.InterNetwork);
    socket.EnableBroadcast=true;socket.Client.Bind(new IPEndPoint(IPAddress.Any,0));
    byte[] ping="cmd=ping\n"u8.ToArray();
    await socket.SendAsync(ping,ping.Length,new IPEndPoint(IPAddress.Broadcast,Port));
    using var cts=CancellationTokenSource.CreateLinkedTokenSource(token);
    cts.CancelAfter(timeout);
    var found=new Dictionary<string,DtracDiscoveredRotor>();
    while(!cts.IsCancellationRequested)
    {
      UdpReceiveResult reply;
      try{reply=await socket.ReceiveAsync(cts.Token);}
      catch(OperationCanceledException) when(!token.IsCancellationRequested){break;}
      if(TryParsePong(reply.Buffer,reply.RemoteEndPoint.Address,out var rotor)&&rotor!=null)
        found[rotor.RotorId]=rotor;
    }
    return found.Values.ToArray();
  }
}