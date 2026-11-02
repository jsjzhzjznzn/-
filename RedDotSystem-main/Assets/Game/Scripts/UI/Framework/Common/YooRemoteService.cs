using System.Collections.Generic;
using YooAsset;

namespace SkierFramework
{
    /// <summary>
    /// HostPlay 远端地址服务：{root}/{package}/{fileName}。
    /// 与服务端发布目录约定一致：{serverRoot}/{package}/ 下放版本文件与 bundle。
    /// </summary>
    public class YooRemoteService : IRemoteService
    {
        private readonly string _rootUrl;

        public YooRemoteService(string rootUrl)
        {
            _rootUrl = string.IsNullOrEmpty(rootUrl) ? string.Empty : rootUrl.TrimEnd('/');
        }

        public IReadOnlyList<string> GetRemoteUrls(string fileName)
        {
            return new List<string> { _rootUrl + "/" + fileName };
        }

        /// <summary>默认按包名分目录：http://127.0.0.1:8899/{package}</summary>
        public static YooRemoteService ForPackage(string packageName, string serverRoot = "http://127.0.0.1:8899")
        {
            return new YooRemoteService(serverRoot.TrimEnd('/') + "/" + packageName);
        }
    }
}
