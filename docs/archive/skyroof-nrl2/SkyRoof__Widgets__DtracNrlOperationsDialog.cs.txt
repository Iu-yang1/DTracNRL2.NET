using System;
using System.Drawing;
using System.Windows.Forms;
namespace SkyRoof;
internal sealed class DtracNrlOperationsDialog:Form
{
  private readonly RotatorWidget rotor;
  private readonly RotatorSettings settings;
  private readonly Button demo=new(){Text="Start DEMO"},stop=new(){Text="STOP DEMO"},park=new(){Text="PARK"},restart=new(){Text="Restart..."};
  private readonly CheckBox single=new(){Text="Single-target PARK (W command)"};
  private readonly Label status=new();
  private readonly System.Windows.Forms.Timer refresh=new(){Interval=250};
  public DtracNrlOperationsDialog(RotatorWidget widget,RotatorSettings s)
  {
    rotor=widget;settings=s;
    Text="DTrac NRL2 Controls";ClientSize=new Size(375,245);
    AutoScaleMode=AutoScaleMode.Font;FormBorderStyle=FormBorderStyle.FixedDialog;
    StartPosition=FormStartPosition.CenterParent;MaximizeBox=false;MinimizeBox=false;
    Controls.Add(new Label{Text="Acceleration / deceleration: RC-3040S firmware",Left=10,Top=12,Width=345});
    single.SetBounds(10,41,345,25);single.Checked=s.DtracUseNativePark;
    single.CheckedChanged+=(_,_)=>{s.DtracUseNativePark=single.Checked;rotor.ctx.Settings.SaveToFile();};
    Controls.Add(single);
    park.SetBounds(10,77,160,28);park.Click+=(_,_)=>rotor.ManualPark();Controls.Add(park);
    restart.SetBounds(196,77,160,28);
    restart.Click+=(_,_)=>{
      if(MessageBox.Show(this,"Experimental restart; keep a physical STOP accessible. Continue?",
        "RC-3040S",MessageBoxButtons.YesNo,MessageBoxIcon.Warning)==DialogResult.Yes)
        rotor.RequestDtracRestart();
    };
    Controls.Add(restart);
    demo.SetBounds(10,121,160,30);
    demo.Click+=(_,_)=>{
      try{rotor.StartDtracDemo();}
      catch(Exception ex) when(ex is ArgumentException or InvalidOperationException)
      {MessageBox.Show(this,ex.Message);}
    };
    Controls.Add(demo);
    stop.SetBounds(196,121,160,30);stop.Click+=(_,_)=>rotor.StopDtracDemo();
    Controls.Add(stop);
    Controls.Add(new Label{Text="DEMO coordinates / dwell / cycles: Settings > Rotator",Left=10,Top=163,Width=355});
    status.SetBounds(10,193,355,32);Controls.Add(status);
    refresh.Tick+=(_,_)=>UpdateStatus();refresh.Start();UpdateStatus();
  }
  private void UpdateStatus()
  {
    if(IsDisposed)return;
    bool ready=rotor.DtracNrlOperationsAvailable;
    park.Enabled=ready;restart.Enabled=ready&&!rotor.IsDtracDemoRunning;
    demo.Enabled=ready&&!rotor.IsDtracDemoRunning;stop.Enabled=rotor.IsDtracDemoRunning;
    status.Text=rotor.IsDtracDemoRunning?"DEMO "+rotor.DtracDemoProgressText:
      ready?"Ready — firmware handles speed ramps":"Offline / tracking / safety lock";
  }
  protected override void Dispose(bool disposing){if(disposing){refresh.Stop();refresh.Dispose();}base.Dispose(disposing);}
}