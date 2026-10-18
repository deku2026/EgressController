using EgressController.SingBox.Core;
using EgressController.Transport.Upstream;

namespace EgressController.SingBox.Runtime;

public static class SingBoxFailure
{
    public static string Code(Exception error, string fallback) => error switch
    {
        SingBoxServiceException service => service.Code,
        SingBoxCoreException core => core.Code,
        DownloadException download => download.Code,
        TimeoutException => "prepare.timeout",
        _ => fallback,
    };
}
