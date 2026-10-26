using PhoneCentre.Adb;
using PhoneCentre.Services;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;

namespace PhoneCentre.Support;

/// <summary>把手機上的檔案拖出到檔案總管：放開時才 adb pull 到暫存（延遲提供），對應 remoteFileItemProvider</summary>
public static class RemoteDrag
{
    public static void Provide(DataPackage data, IReadOnlyList<RemoteFile> files, Device device)
    {
        data.RequestedOperation = DataPackageOperation.Copy;
        data.SetDataProvider(StandardDataFormats.StorageItems, async request =>
        {
            var deferral = request.GetDeferral();
            try
            {
                var result = new List<IStorageItem>();
                foreach (var f in files)
                {
                    var tmp = Path.Combine(Path.GetTempPath(), "PhoneCentre-drag-" + Guid.NewGuid().ToString("N"));
                    Directory.CreateDirectory(tmp);
                    var dest = Path.Combine(tmp, f.Name);
                    await device.Client.PullAsync(f.Path, dest);
                    result.Add(f.IsDir ? await StorageFolder.GetFolderFromPathAsync(dest) : await StorageFile.GetFileFromPathAsync(dest));
                }
                request.SetData(result);
            }
            finally { deferral.Complete(); }
        });
    }
}
