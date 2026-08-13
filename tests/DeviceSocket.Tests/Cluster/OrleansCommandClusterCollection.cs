namespace DeviceSocket.Tests.Cluster;

/// <summary>
/// 让命令闭环用例共享一个测试 Silo，并禁止这些状态相关用例并行互相干扰。
/// </summary>
[CollectionDefinition(Name)]
public sealed class OrleansCommandClusterCollection : ICollectionFixture<OrleansCommandClusterFixture>
{
    /// <summary>测试集合名称。</summary>
    public const string Name = "OrleansCommandCluster";
}
