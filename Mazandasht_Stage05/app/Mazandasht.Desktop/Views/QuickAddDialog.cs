using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Mazandasht.Desktop.Views
{
    public sealed class QuickAddDialog : Window
    {
        private readonly List<TextBox> _fields = new List<TextBox>();
        public string[] Values { get; private set; }

        public QuickAddDialog(string title, params string[] labels)
        {
            Title=title; Width=460; SizeToContent=SizeToContent.Height; MinHeight=220;
            WindowStartupLocation=WindowStartupLocation.CenterOwner; ResizeMode=ResizeMode.NoResize;
            FlowDirection=FlowDirection.RightToLeft; Background=new SolidColorBrush(Color.FromRgb(11,23,17));
            var root=new StackPanel { Margin=new Thickness(22) };
            root.Children.Add(new TextBlock { Text=title, FontSize=20, FontWeight=FontWeights.Bold, Foreground=Brushes.White, Margin=new Thickness(0,0,0,14) });
            foreach(var label in labels)
            {
                root.Children.Add(new TextBlock { Text=label, Foreground=Brushes.White, Margin=new Thickness(2,8,2,4) });
                var box=new TextBox { Height=36, Padding=new Thickness(8), Background=new SolidColorBrush(Color.FromRgb(29,48,38)),
                    Foreground=Brushes.White, BorderBrush=new SolidColorBrush(Color.FromRgb(90,125,100)) };
                _fields.Add(box); root.Children.Add(box);
            }
            var save=new Button { Content="ثبت", Height=40, Margin=new Thickness(0,18,0,0),
                Background=new SolidColorBrush(Color.FromRgb(212,105,9)), Foreground=Brushes.White };
            save.Click+=(s,e)=>{
                if(_fields.Count>0 && String.IsNullOrWhiteSpace(_fields[0].Text))
                { MessageBox.Show("فیلد اول الزامی است."); return; }
                Values=_fields.ConvertAll(x=>x.Text.Trim()).ToArray(); DialogResult=true;
            };
            root.Children.Add(save); Content=root;
        }
    }
}
