using System;
using System.ComponentModel;
using System.Drawing.Design;
using System.Linq;
using System.Windows.Forms;
namespace SkyRoof.NativeRotator;
public sealed class DtracRotorIdEditor:UITypeEditor
{
  public override UITypeEditorEditStyle GetEditStyle(ITypeDescriptorContext? context)=>UITypeEditorEditStyle.Modal;
  public override object? EditValue(ITypeDescriptorContext? context,IServiceProvider? provider,object? value)
  {
    using var form=new Form{Text="Discover RC-3040S (UDP 60059)",Width=480,Height=290,
      FormBorderStyle=FormBorderStyle.FixedDialog,StartPosition=FormStartPosition.CenterParent,MinimizeBox=false,MaximizeBox=false};
    var label=new Label{Left=12,Top=12,Width=450,Text="Scanning local network..."};
    var list=new ListBox{Left=12,Top=38,Width=440,Height=156};
    var ok=new Button{Text="Select ID",Left=316,Top=205,Width=136,DialogResult=DialogResult.OK,Enabled=false};
    var cancel=new Button{Text="Cancel",Left=200,Top=205,Width=105,DialogResult=DialogResult.Cancel};
    form.Controls.AddRange(new Control[]{label,list,ok,cancel});
    form.AcceptButton=ok;form.CancelButton=cancel;
    DtracDiscoveredRotor[] rotors=Array.Empty<DtracDiscoveredRotor>();
    list.SelectedIndexChanged+=(_,_)=>ok.Enabled=list.SelectedIndex>=0;
    form.Shown+=async (_,_)=>
    {
      try{
        rotors=(await DtracDiscovery.DiscoverAsync(TimeSpan.FromSeconds(3))).ToArray();
        if(form.IsDisposed)return;
        foreach(var r in rotors)list.Items.Add($"{r.Address}:{r.TcpPort}  {r.RotorId}");
        label.Text=rotors.Length==0?"No RC-3040S found; enter ID manually":"Select RC-3040S:";
      }catch(Exception e){if(!form.IsDisposed)label.Text=e.Message;}
    };
    return form.ShowDialog()==DialogResult.OK && list.SelectedIndex>=0?rotors[list.SelectedIndex].RotorId:value;
  }
}