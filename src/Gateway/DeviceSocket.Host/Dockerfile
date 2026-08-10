FROM mcr.microsoft.com/dotnet/aspnet:8.0

WORKDIR /app

# 发布由 build-docker-image.ps1 在宿主机完成，镜像只复制可运行产物。
COPY --chown=app:app bin/Release/net8.0/publish/ ./

# 日志目录需要允许 .NET 8 官方镜像内置的非 root 用户 app 写入。
RUN mkdir -p /app/logs \
    && chown app:app /app/logs

USER app

# EXPOSE 只声明镜像能力；Linux host 网络模式直接使用宿主机端口。
EXPOSE 5001 5002 5080 7001

ENTRYPOINT ["dotnet", "DeviceSocket.Host.dll"]
