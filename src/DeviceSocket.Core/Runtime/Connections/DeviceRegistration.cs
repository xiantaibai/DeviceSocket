namespace DeviceSocket.Core.Runtime.Connections;

/// <summary>
/// 设备注册结果；重复登录时返回被新连接替换的旧连接。
/// </summary>
public sealed record DeviceRegistration(DeviceConnectionSnapshot Current, IDeviceConnection? ReplacedConnection);
