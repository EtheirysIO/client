using System.Collections.Generic;
using System.Threading.Tasks;
using EtheirysSynchronos.API;
using Microsoft.AspNetCore.SignalR.Client;

namespace EtheirysSynchronos.WebAPI
{
    public partial class ApiController
    {
        public async Task AddOrUpdateForbiddenFileEntry(ForbiddenFileDto forbiddenFile)
        {
            await _ethHub!.SendAsync(Api.SendAdminUpdateOrAddForbiddenFile, forbiddenFile);
        }

        public async Task DeleteForbiddenFileEntry(ForbiddenFileDto forbiddenFile)
        {
            await _ethHub!.SendAsync(Api.SendAdminDeleteForbiddenFile, forbiddenFile);
        }

        public async Task AddOrUpdateBannedUserEntry(BannedUserDto bannedUser)
        {
            await _ethHub!.SendAsync(Api.SendAdminUpdateOrAddBannedUser, bannedUser);
        }

        public async Task DeleteBannedUserEntry(BannedUserDto bannedUser)
        {
            await _ethHub!.SendAsync(Api.SendAdminDeleteBannedUser, bannedUser);
        }

        public async Task RefreshOnlineUsers()
        {
            AdminOnlineUsers = await _ethHub!.InvokeAsync<List<OnlineUserDto>>(Api.InvokeAdminGetOnlineUsers);
        }

        public List<OnlineUserDto> AdminOnlineUsers { get; set; } = new List<OnlineUserDto>();

        public void PromoteToModerator(string onlineUserUID)
        {
            _ethHub!.SendAsync(Api.SendAdminChangeModeratorStatus, onlineUserUID, true);
        }

        public void DemoteFromModerator(string onlineUserUID)
        {
            _ethHub!.SendAsync(Api.SendAdminChangeModeratorStatus, onlineUserUID, false);
        }
    }
}
