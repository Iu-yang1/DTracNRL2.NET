using System;
using System.ComponentModel;
using System.Linq;
using System.Net;
using System.Text;
using SkyRoof;
using SkyRoof.NativeRotator;
using Xunit;
namespace VE3NEA.Dsp.Tests;
public sealed class DtracNrl2Tests
{
  const string Sid="01020304050607",Rid="123456789ABCDE";
  [Theory]
  [InlineData("C2\r\n",52)]
  [InlineData("W181 030\r\n",58)]
  [InlineData("S\r\n",51)]
  public void ApkMessageLayout(string command,int length)
  {
    var frame=DtracNrl2Codec.BuildCommand(Sid,Rid,Encoding.ASCII.GetBytes(command));
    Assert.Equal(length,frame.Length);
    Assert.Equal("NRL2",Encoding.ASCII.GetString(frame,0,4));
    Assert.Equal(length.ToString(),Encoding.ASCII.GetString(frame,4,2));
    Assert.Equal(5,frame[20]);
    Assert.Equal(Rid,Convert.ToHexString(frame.AsSpan(13,7)));
    Assert.Equal(command,Encoding.ASCII.GetString(frame,48,frame.Length-48));
    Assert.False(DtracNrl2Codec.TryDecodeReply(frame,Rid,Sid,out _));
  }
  [Fact]
  public void Nrl2LegacySettingsRemainButAreSafelyDisabledAtLoad()
  {
    var settings = new RotatorSettings
    {
      DriverMode = RotatorDriverMode.Native,
      NativeProtocol = RotatorProtocolKind.DtracNrl2,
      Enabled = true
    };
    settings.MigrateDriverMode();
    Assert.False(settings.Enabled);
    Assert.Equal(RotatorProtocolKind.DtracNrl2,settings.NativeProtocol);
    Assert.False(NativeRotatorCodec.Supports(RotatorProtocolKind.DtracNrl2,RotatorTransportKind.Udp));
    var converter = new RotatorProtocolSelectionConverter();
    var offered = converter.GetStandardValues(null)!.Cast<RotatorProtocolKind>().ToArray();
    Assert.DoesNotContain(RotatorProtocolKind.DtracNrl2,offered);
    Assert.Contains(RotatorProtocolKind.Rc3040sLan,offered);
  }

  [Fact] public void RepliesAreMatchedToBothIdentifiers()
  {
    var frame=DtracNrl2Codec.BuildCommand(Rid,Sid,"AZ=180  EL=030\r\n"u8);
    Assert.True(DtracNrl2Codec.TryDecodeReply(frame,Rid,Sid,out var bytes));
    Assert.Equal("AZ=180  EL=030\r\n",Encoding.ASCII.GetString(bytes));
    frame[20]=6;
    Assert.False(DtracNrl2Codec.TryDecodeReply(frame,Rid,Sid,out _));
  }
  [Theory]
  [InlineData("cmd=pong&type=RC-3040S&ip=192.0.2.10&port=60020&rotorid=123456789ABCDE\n")]
  [InlineData("cmd=pong&type=1&ip=192.0.2.10&port=60020&id=123456789ABCDE\n")]
  public void DiscoveryParsesOldAndNewFormats(string message)
  {
    Assert.True(DtracDiscovery.TryParsePong(Encoding.ASCII.GetBytes(message),IPAddress.Parse("192.0.2.10"),out var dev));
    Assert.Equal(Rid,dev!.RotorId);
  }
  [Fact] public void ProtocolAndSettingsExposeDedicatedUdpMode()
  {
    var settings=new RotatorSettings{NativeProtocol=RotatorProtocolKind.DtracNrl2};
    Assert.Equal(RotatorTransportKind.Udp,settings.NativeTransport);
    // NRL2 wire-format tests remain, but operational UDP is hidden/disabled.
    Assert.False(NativeRotatorCodec.Supports(RotatorProtocolKind.DtracNrl2,RotatorTransportKind.Udp));
    Assert.False(NativeRotatorCodec.Supports(RotatorProtocolKind.DtracNrl2,RotatorTransportKind.Tcp));
    Assert.Equal("C2\r\n",Encoding.ASCII.GetString(NativeRotatorCodec.Query(RotatorProtocolKind.DtracNrl2)!));
    Assert.Equal("S\r\n",Encoding.ASCII.GetString(NativeRotatorCodec.Stop(RotatorProtocolKind.DtracNrl2)!));
    Assert.Equal("W181 030\r\n",Encoding.ASCII.GetString(NativeRotatorCodec.Point(RotatorProtocolKind.DtracNrl2,new(181,30))));
    var props=new RotatorSettingsPropertyConverter()
      .GetProperties(null,settings,new Attribute[]{BrowsableAttribute.Yes})
      .Cast<PropertyDescriptor>().Select(p=>p.Name).ToArray();
    Assert.Contains(nameof(RotatorSettings.DtracNrlRotorId),props);
    Assert.DoesNotContain(nameof(RotatorSettings.Host),props);
  }
}