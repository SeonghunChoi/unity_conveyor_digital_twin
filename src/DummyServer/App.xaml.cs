using System.IO;
using System.Windows;
using ConveyorTwin.DummyServer.OpcUa;
using ConveyorTwin.Shared;

namespace ConveyorTwin.DummyServer;

public partial class App : Application
{
    /// <summary>
    /// 인수 없이 실행하면 서버 창을 띄운다.
    /// `--provision-twin-cert &lt;트윈 pki&gt;`이면 창 없이 트윈 OPC UA 인증서만 발급하고 종료한다 (종료 코드 0 = 성공).
    /// 결과는 &lt;트윈 pki&gt;/provision.log에 남긴다 (WinExe라 콘솔 출력이 보이지 않음).
    /// </summary>
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        if (e.Args.Length == 2 && e.Args[0] == OpcUaEndpoint.ProvisionArg)
        {
            var twinPki = Path.GetFullPath(e.Args[1]);
            int code;
            string message;
            try
            {
                message = Task.Run(() => CertificateProvisioning.ProvisionTwinAsync(twinPki)).GetAwaiter().GetResult();
                code = 0;
            }
            catch (Exception ex)
            {
                message = "실패: " + ex.GetBaseException().Message;
                code = 1;
            }
            try
            {
                Directory.CreateDirectory(twinPki);
                File.AppendAllText(Path.Combine(twinPki, "provision.log"), $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {message}{Environment.NewLine}");
            }
            catch (IOException) { }
            Shutdown(code);
            return;
        }

        new MainWindow().Show();
    }
}
