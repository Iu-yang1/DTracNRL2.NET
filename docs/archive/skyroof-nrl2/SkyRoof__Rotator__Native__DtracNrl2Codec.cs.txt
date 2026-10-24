using System;
using System.Globalization;
using System.Text;
namespace SkyRoof.NativeRotator;
public static class DtracNrl2Codec
{
  public const int HeaderSize=48;
  public static byte[] ParseIdentifier(string value)
  {
    if(value is null || value.Length!=14) throw new ArgumentException("Expected fourteen hexadecimal digits");
    try{return Convert.FromHexString(value);}
    catch(FormatException e){throw new ArgumentException("Invalid identifier",e);}
  }
  public static byte[] PositionCommand(RotatorPosition position)
  {
    double az=position.Azimuth<0?(position.Azimuth%360+360)%360:Math.Min(450,position.Azimuth);
    int a=(int)Math.Round(az,MidpointRounding.AwayFromZero);
    int e=(int)Math.Round(Math.Clamp(position.Elevation,0,180),MidpointRounding.AwayFromZero);
    return Encoding.ASCII.GetBytes($"W{a:000} {e:000}\r\n");
  }
  public static byte[] BuildCommand(string sid,string rotorId,ReadOnlySpan<byte> payload)
  {
    byte[] client=ParseIdentifier(sid),device=ParseIdentifier(rotorId);
    int len=checked(48+payload.Length);
    if(len>99)throw new ArgumentOutOfRangeException(nameof(payload));
    byte[] b=new byte[len];
    "NRL2"u8.CopyTo(b.AsSpan(0,4));
    string size=len.ToString("D2",CultureInfo.InvariantCulture);
    b[4]=(byte)size[0];b[5]=(byte)size[1];
    client.CopyTo(b.AsSpan(6,7));device.CopyTo(b.AsSpan(13,7));
    b[20]=5;
    "DTrac"u8.CopyTo(b.AsSpan(24,5));
    payload.CopyTo(b.AsSpan(48));
    return b;
  }
  public static bool TryDecodeReply(ReadOnlySpan<byte> packet,string rotorId,string sid,out byte[] payload)
  {
    payload=Array.Empty<byte>();
    if(packet.Length is <49 or >99 || !packet[..4].SequenceEqual("NRL2"u8) ||
       packet[4] is <48 or >57 || packet[5] is <48 or >57 ||
       (packet[4]-48)*10+packet[5]-48!=packet.Length || packet[20]!=5)return false;
    byte[] device,client;
    try{device=ParseIdentifier(rotorId);client=ParseIdentifier(sid);}catch(ArgumentException){return false;}
    if(!packet.Slice(6,7).SequenceEqual(device)||!packet.Slice(13,7).SequenceEqual(client))return false;
    var body=packet[48..];
    foreach(byte b in body)if(b is not (10 or 13) && (b<32||b>126))return false;
    payload=body.ToArray();
    return true;
  }
}