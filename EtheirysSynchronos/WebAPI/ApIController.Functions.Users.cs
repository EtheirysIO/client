using System.Collections.Generic;
using System.Threading.Tasks;
using EtheirysSynchronos.API;
using EtheirysSynchronos.Utils;
using Microsoft.AspNetCore.SignalR.Client;

namespace EtheirysSynchronos.WebAPI
{
    public partial class ApiController
    {
        public async Task DeleteAccount()
        {
            _pluginConfiguration.ClientSecret.Remove(ApiUri);
            _pluginConfiguration.Save();
            await _ethHub!.SendAsync(Api.SendFileDeleteAllFiles);
            await _ethHub!.SendAsync(Api.SendUserDeleteAccount);
            await CreateConnections();
        }

        public async Task Register(bool isIntroUi = false)
        {
            if (!ServerAlive) return;
            Logger.Debug("Registering at service " + ApiUri);
            var response = await _ethHub!.InvokeAsync<string>(Api.InvokeUserRegister);
            _pluginConfiguration.ClientSecret[ApiUri] = response;
            _pluginConfiguration.Save();
            if (!isIntroUi)
            {
                RegisterFinalized?.Invoke();
            }

            await CreateConnections();
        }

        public async Task<List<string>> GetOnlineCharacters()
        {
            return await _ethHub!.InvokeAsync<List<string>>(Api.InvokeUserGetOnlineCharacters);
        }

        public async Task SendPairedClientAddition(string uid)
        {
            if (!IsConnected || SecretKey == "-") return;
            await _ethHub!.SendAsync(Api.SendUserPairedClientAddition, uid);
        }

        public async Task SendPairedClientPauseChange(string uid, bool paused)
        {
            if (!IsConnected || SecretKey == "-") return;
            await _ethHub!.SendAsync(Api.SendUserPairedClientPauseChange, uid, paused);
        }

        public async Task SendPairedClientRemoval(string uid)
        {
            if (!IsConnected || SecretKey == "-") return;
            await _ethHub!.SendAsync(Api.SendUserPairedClientRemoval, uid);
        }
    }

}
