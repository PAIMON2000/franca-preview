using System.IO;
using System.Net.Http;
using System.Windows;
using Franca.Models;
using Franca.Services;

namespace Franca.Diagnostics;

internal static partial class ProbeRunner
{
    private static void RunProbeDialog()
    {
        var dialog = new Views.CustomEndpointDialog();
        // 1. 空表单点保存：应被校验拦下（Result 为空、ErrorText 显示）
        InvokeSave(dialog);
        Console.WriteLine($"empty save: Result={(dialog.Result is null ? "null(拦截)" : "set")}, ErrorVisible={dialog.ErrorText.Visibility}");
        // 2. 填名称和 BaseUrl 点保存：应产出 CustomEndpointDef
        dialog.NameBox.Text = "Probe-Endpoint";
        dialog.BaseUrlBox.Text = "http://127.0.0.1:9/v1/";
        InvokeSave(dialog);
        var r = dialog.Result;
        Console.WriteLine(r is null
            ? "filled save: FAILED - Result still null"
            : $"filled save: Id={r.Id}, Name={r.Name}, Type={r.Type}, BaseUrl={r.BaseUrl}, DialogResult={dialog.DialogResult}");
    }

    private static void InvokeSave(Views.CustomEndpointDialog dialog)
    {
        var method = typeof(Views.CustomEndpointDialog).GetMethod("OnSaveClick",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        try
        {
            method!.Invoke(dialog, new object?[] { null, null });
        }
        catch (System.Reflection.TargetInvocationException ex) when (ex.InnerException is InvalidOperationException)
        {
            // 无头环境窗口未作为对话框显示，DialogResult 赋值必抛；Result 在该行之前已赋值，忽略
        }
    }


}
