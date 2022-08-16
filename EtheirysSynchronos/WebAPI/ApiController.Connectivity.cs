﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using EtheirysSynchronos.API;
using EtheirysSynchronos.Utils;
using EtheirysSynchronos.WebAPI.Utils;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;

namespace EtheirysSynchronos.WebAPI
{
    public delegate void VoidDelegate();
    public delegate void SimpleStringDelegate(string str);
    public enum ServerState
    {
        Offline,
        Disconnected,
        Connected,
        Unauthorized,
        VersionMisMatch,
        RateLimited,
        NoAccount
    }

    public partial class ApiController : IDisposable
    {
        public const string MainServer = "Etheirys (US/CAN Only)";
        public const string MainServiceUri = "wss://sync.etheirys.io:2096";

        public readonly int[] SupportedServerVersions = { Api.Version };

        private readonly Configuration _pluginConfiguration;
        private readonly DalamudUtil _dalamudUtil;

        private CancellationTokenSource _connectionCancellationTokenSource;

        private HubConnection? _ethHub;

        private CancellationTokenSource? _uploadCancellationTokenSource = new();

        private ConnectionDto? _connectionDto;
        public SystemInfoDto SystemInfoDto { get; private set; } = new();
        public bool IsModerator => (_connectionDto?.IsAdmin ?? false) || (_connectionDto?.IsModerator ?? false);

        public bool IsAdmin => _connectionDto?.IsAdmin ?? false;

        public ApiController(Configuration pluginConfiguration, DalamudUtil dalamudUtil)
        {
            Logger.Verbose("Creating " + nameof(ApiController));

            _pluginConfiguration = pluginConfiguration;
            _dalamudUtil = dalamudUtil;
            _connectionCancellationTokenSource = new CancellationTokenSource();
            _dalamudUtil.LogIn += DalamudUtilOnLogIn;
            _dalamudUtil.LogOut += DalamudUtilOnLogOut;
            ServerState = ServerState.Offline;
            _verifiedUploadedHashes = new();

            if (_dalamudUtil.IsLoggedIn)
            {
                DalamudUtilOnLogIn();
            }
        }

        private void DalamudUtilOnLogOut()
        {
            Task.Run(async () => await StopConnection(_connectionCancellationTokenSource.Token));
        }

        private void DalamudUtilOnLogIn()
        {
            Task.Run(CreateConnections);
        }


        public event EventHandler<CharacterReceivedEventArgs>? CharacterReceived;

        public event VoidDelegate? RegisterFinalized;

        public event VoidDelegate? Connected;

        public event VoidDelegate? Disconnected;

        public event SimpleStringDelegate? PairedClientOffline;

        public event SimpleStringDelegate? PairedClientOnline;

        public event SimpleStringDelegate? PairedWithOther;

        public event SimpleStringDelegate? UnpairedFromOther;

        public Dictionary<int, List<DownloadFileTransfer>> CurrentDownloads { get; } = new();

        public List<FileTransfer> CurrentUploads { get; } = new();

        public List<FileTransfer> ForbiddenTransfers { get; } = new();

        public List<BannedUserDto> AdminBannedUsers { get; private set; } = new();

        public List<ForbiddenFileDto> AdminForbiddenFiles { get; private set; } = new();

        public bool IsConnected => ServerState == ServerState.Connected;

        public bool IsDownloading => CurrentDownloads.Count > 0;

        public bool IsUploading => CurrentUploads.Count > 0;

        public List<ClientPairDto> PairedClients { get; set; } = new();

        public string SecretKey => _pluginConfiguration.ClientSecret.ContainsKey(ApiUri)
            ? _pluginConfiguration.ClientSecret[ApiUri] : string.Empty;

        public bool ServerAlive => ServerState is ServerState.Connected or ServerState.RateLimited or ServerState.Unauthorized or ServerState.Disconnected or ServerState.NoAccount;

        public Dictionary<string, string> ServerDictionary => new Dictionary<string, string>()
                { { MainServiceUri, MainServer } }
            .Concat(_pluginConfiguration.CustomServerList)
            .ToDictionary(k => k.Key, k => k.Value);

        public string UID => _connectionDto?.UID ?? string.Empty;
        private string ApiUri => _pluginConfiguration.ApiUri;
        public int OnlineUsers => SystemInfoDto.OnlineUsers;

        public ServerState ServerState { get; private set; }

        public async Task CreateConnections()
        {
            if (_pluginConfiguration.FullPause)
            {
                ServerState = ServerState.Disconnected;
                _connectionDto = null;
                return;
            }
            Logger.Info("Recreating Connection");

            await StopConnection(_connectionCancellationTokenSource.Token);

            _connectionCancellationTokenSource.Cancel();
            _connectionCancellationTokenSource = new CancellationTokenSource();
            var token = _connectionCancellationTokenSource.Token;
            _verifiedUploadedHashes.Clear();
            while (ServerState is not ServerState.Connected && !token.IsCancellationRequested)
            {
                if (string.IsNullOrEmpty(SecretKey))
                {
                    await Task.Delay(TimeSpan.FromSeconds(2));
                    continue;
                }

                await StopConnection(token);

                try
                {
                    Logger.Debug("Building connection");

                    while (!_dalamudUtil.IsPlayerPresent && !token.IsCancellationRequested)
                    {
                        Logger.Debug("Player not loaded in yet, waiting");
                        await Task.Delay(TimeSpan.FromSeconds(1), token);
                    }

                    if (token.IsCancellationRequested) break;

                    _ethHub = BuildHubConnection(Api.Path);

                    await _ethHub.StartAsync(token);

                    _ethHub.On<SystemInfoDto>(Api.OnUpdateSystemInfo, (dto) => SystemInfoDto = dto);

                    _connectionDto =
                        await _ethHub.InvokeAsync<ConnectionDto>(Api.InvokeHeartbeat, _dalamudUtil.PlayerNameHashed, token);

                    ServerState = ServerState.Connected;

                    if (_connectionDto.ServerVersion != Api.Version)
                    {
                        ServerState = ServerState.VersionMisMatch;
                        await StopConnection(token);
                        return;
                    }
                    if (ServerState is ServerState.Connected) // user is authorized && server is legit
                    {
                        await InitializeData(token);

                        _ethHub.Closed += EthHubOnClosed;
                        _ethHub.Reconnected += EthHubOnReconnected;
                        _ethHub.Reconnecting += EthHubOnReconnecting;
                    }
                }
                catch (HubException ex)
                {
                    Logger.Warn(ex.GetType().ToString());
                    Logger.Warn(ex.Message);
                    Logger.Warn(ex.StackTrace ?? string.Empty);

                    ServerState = ServerState.RateLimited;
                    await StopConnection(token);
                    return;
                }
                catch (HttpRequestException ex)
                {
                    Logger.Warn(ex.GetType().ToString());
                    Logger.Warn(ex.Message);
                    Logger.Warn(ex.StackTrace ?? string.Empty);

                    if (ex.StatusCode == System.Net.HttpStatusCode.Unauthorized)
                    {
                        ServerState = ServerState.Unauthorized;
                        await StopConnection(token);
                        return;
                    }
                    else
                    {
                        ServerState = ServerState.Offline;
                        Logger.Info("Failed to establish connection, retrying");
                        await Task.Delay(TimeSpan.FromSeconds(new Random().Next(5, 20)), token);
                    }
                }
                catch (Exception ex)
                {
                    Logger.Warn(ex.GetType().ToString());
                    Logger.Warn(ex.Message);
                    Logger.Warn(ex.StackTrace ?? string.Empty);
                    Logger.Info("Failed to establish connection, retrying");
                    await Task.Delay(TimeSpan.FromSeconds(new Random().Next(5, 20)), token);
                }
            }
        }
        private async Task InitializeData(CancellationToken token)
        {
            if (_ethHub == null) return;

            Logger.Debug("Initializing data");
            _ethHub.On<ClientPairDto, string>(Api.OnUserUpdateClientPairs,
                UpdateLocalClientPairsCallback);
            _ethHub.On<CharacterCacheDto, string>(Api.OnUserReceiveCharacterData,
                ReceiveCharacterDataCallback);
            _ethHub.On<string>(Api.OnUserRemoveOnlinePairedPlayer,
                (s) => PairedClientOffline?.Invoke(s));
            _ethHub.On<string>(Api.OnUserAddOnlinePairedPlayer,
                (s) => PairedClientOnline?.Invoke(s));
            _ethHub.On(Api.OnAdminForcedReconnect, UserForcedReconnectCallback);

            PairedClients =
                await _ethHub!.InvokeAsync<List<ClientPairDto>>(Api.InvokeUserGetPairedClients, token);

            if (IsModerator)
            {
                AdminForbiddenFiles =
                    await _ethHub.InvokeAsync<List<ForbiddenFileDto>>(Api.InvokeAdminGetForbiddenFiles,
                        token);
                AdminBannedUsers =
                    await _ethHub.InvokeAsync<List<BannedUserDto>>(Api.InvokeAdminGetBannedUsers,
                        token);
                _ethHub.On<BannedUserDto>(Api.OnAdminUpdateOrAddBannedUser,
                    UpdateOrAddBannedUserCallback);
                _ethHub.On<BannedUserDto>(Api.OnAdminDeleteBannedUser, DeleteBannedUserCallback);
                _ethHub.On<ForbiddenFileDto>(Api.OnAdminUpdateOrAddForbiddenFile,
                    UpdateOrAddForbiddenFileCallback);
                _ethHub.On<ForbiddenFileDto>(Api.OnAdminDeleteForbiddenFile,
                    DeleteForbiddenFileCallback);
            }

            Connected?.Invoke();
        }

        public void Dispose()
        {
            Logger.Verbose("Disposing " + nameof(ApiController));

            _dalamudUtil.LogIn -= DalamudUtilOnLogIn;
            _dalamudUtil.LogOut -= DalamudUtilOnLogOut;

            Task.Run(async () => await StopConnection(_connectionCancellationTokenSource.Token));
            _connectionCancellationTokenSource?.Cancel();
        }

        private HubConnection BuildHubConnection(string hubName)
        {
            return new HubConnectionBuilder()
                .WithUrl(ApiUri + hubName, options =>
                {
                    if (!string.IsNullOrEmpty(SecretKey) && !_pluginConfiguration.FullPause)
                    {
                        options.Headers.Add("Authorization", SecretKey);
                    }

                    options.Transports = HttpTransportType.WebSockets;
                })
                .WithAutomaticReconnect(new ForeverRetryPolicy())
                .Build();
        }

        private Task EthHubOnClosed(Exception? arg)
        {
            CurrentUploads.Clear();
            CurrentDownloads.Clear();
            _uploadCancellationTokenSource?.Cancel();
            Logger.Info("Connection closed");
            Disconnected?.Invoke();
            return Task.CompletedTask;
        }

        private async Task EthHubOnReconnected(string? arg)
        {
            Logger.Info("Connection restored");
            await Task.Delay(TimeSpan.FromSeconds(new Random().Next(5, 10)));

            _ = Task.Run(CreateConnections);
        }

        private Task EthHubOnReconnecting(Exception? arg)
        {
            CurrentUploads.Clear();
            CurrentDownloads.Clear();
            _uploadCancellationTokenSource?.Cancel();
            Logger.Warn("Connection closed... Reconnecting");
            Logger.Warn(arg?.Message ?? string.Empty);
            Logger.Warn(arg?.StackTrace ?? string.Empty);
            Disconnected?.Invoke();
            return Task.CompletedTask;
        }

        private async Task StopConnection(CancellationToken token)
        {
            if (_ethHub is not null)
            {
                Logger.Info("Stopping all connections");
                await _ethHub.StopAsync(token);
                _ethHub.Closed -= EthHubOnClosed;
                _ethHub.Reconnected -= EthHubOnReconnected;
                _ethHub.Reconnecting += EthHubOnReconnecting;
                await _ethHub.DisposeAsync();
                _ethHub = null;
            }
        }
    }
}
