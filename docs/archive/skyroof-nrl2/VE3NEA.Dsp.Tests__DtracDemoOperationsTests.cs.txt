using System;
using SkyRoof.NativeRotator;
using Xunit;
namespace VE3NEA.Dsp.Tests;
public sealed class DtracDemoOperationsTests
{
  private static readonly DateTime T0=new(2026,10,24,0,0,0,DateTimeKind.Utc);
  [Fact] public void CruiseRequiresTwoFreshReadingsAndReturnsToStart()
  {
    var s=DtracDemoSequence.Create(0,20,30,10,1,2,0,360,0,90,T0);
    Assert.Equal(5,s.Count);
    Assert.Equal(DtracDemoProgress.Waiting,s.Observe(T0.AddSeconds(1),T0.AddSeconds(1),0,30,true));
    Assert.Equal(DtracDemoProgress.NextWaypoint,s.Observe(T0.AddSeconds(3),T0.AddSeconds(3),0,30,true));
    Assert.Equal(10,s.Current.Azimuth);
  }
  [Fact] public void StopOnBadStatusOrTimeout()
  {
    var s=DtracDemoSequence.Create(0,20,30,10,1,1,0,360,0,90,T0);
    Assert.Equal(DtracDemoProgress.Aborted,s.Observe(T0.AddSeconds(1),T0.AddSeconds(1),0,30,false));
    var timeout=DtracDemoSequence.Create(0,20,30,10,1,1,0,360,0,90,T0);
    Assert.Equal(DtracDemoProgress.Aborted,timeout.Observe(T0.AddSeconds(121),T0.AddSeconds(121),0,30,true));
  }
  [Fact] public void RejectsUnsafeSegments()
  {
    Assert.Throws<ArgumentOutOfRangeException>(()=>DtracDemoSequence.Create(0,450,30,10,1,1,0,360,0,90,T0));
  }
}