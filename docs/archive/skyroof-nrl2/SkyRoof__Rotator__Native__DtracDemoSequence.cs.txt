using System;
using System.Collections.Generic;
namespace SkyRoof.NativeRotator;

/// <summary>Finite SkyRoof-side cruise; firmware performs its own acceleration on each W move.</summary>
public enum DtracDemoProgress{Waiting,NextWaypoint,Completed,Aborted}
public sealed class DtracDemoSequence
{
  private readonly RotatorPosition[] waypoints;
  private readonly TimeSpan dwell;
  private DateTime segmentStart,lastObservation,arrivedUtc;
  private int arrivals;
  public int Index{get;private set;}
  public int Count=>waypoints.Length;
  public RotatorPosition Current=>waypoints[Index];
  private DtracDemoSequence(RotatorPosition[] points,TimeSpan dwellTime,DateTime now)
  {waypoints=points;dwell=dwellTime;segmentStart=now;}
  public static DtracDemoSequence Create(double startAz,double endAz,double elevation,double step,
    int cycles,int dwellSeconds,double minAz,double maxAz,double minEl,double maxEl,DateTime now)
  {
    if(!double.IsFinite(startAz)||!double.IsFinite(endAz)||!double.IsFinite(elevation)||
       !double.IsFinite(step)||!double.IsFinite(minAz)||!double.IsFinite(maxAz)||
       !double.IsFinite(minEl)||!double.IsFinite(maxEl)||
       startAz<minAz||endAz>maxAz||startAz>=endAz||elevation<minEl||elevation>maxEl||
       step is <1 or >90 || cycles is <1 or >5 || dwellSeconds is <1 or >60)
      throw new ArgumentOutOfRangeException(nameof(startAz),"Unsafe DEMO parameters");
    var azs=new List<double>{startAz};
    for(double az=startAz+step;az<endAz-1e-8;az+=step)
    {
      if(azs.Count>450)throw new ArgumentOutOfRangeException(nameof(step));
      azs.Add(az);
    }
    azs.Add(endAz);
    var points=new List<RotatorPosition>();
    for(int cycle=0;cycle<cycles;cycle++)
    {
      foreach(double a in azs)points.Add(new RotatorPosition(a,elevation));
      for(int i=azs.Count-2;i>=0;i--)points.Add(new RotatorPosition(azs[i],elevation));
    }
    return new DtracDemoSequence(points.ToArray(),TimeSpan.FromSeconds(dwellSeconds),now);
  }
  public DtracDemoProgress Observe(DateTime now,DateTime readUtc,double actualAz,double actualEl,bool healthy)
  {
    if(!healthy||now-segmentStart>TimeSpan.FromSeconds(120))return DtracDemoProgress.Aborted;
    if(readUtc<=lastObservation||readUtc>now||now-readUtc>TimeSpan.FromSeconds(3))
      return DtracDemoProgress.Waiting;
    lastObservation=readUtc;
    if(!double.IsFinite(actualAz)||!double.IsFinite(actualEl)||
      Math.Abs(actualAz-Current.Azimuth)>1.5||Math.Abs(actualEl-Current.Elevation)>1.5)
    {
      arrivals=0;arrivedUtc=default;return DtracDemoProgress.Waiting;
    }
    if(++arrivals==1)arrivedUtc=now;
    if(arrivals<2||now-arrivedUtc<dwell)return DtracDemoProgress.Waiting;
    if(++Index==waypoints.Length)return DtracDemoProgress.Completed;
    arrivals=0;arrivedUtc=default;segmentStart=now;return DtracDemoProgress.NextWaypoint;
  }
}