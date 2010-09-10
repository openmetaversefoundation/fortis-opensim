/*
 * Copyright (c) Contributors, http://opensimulator.org/
 * See CONTRIBUTORS.TXT for a full list of copyright holders.
 *
 * Redistribution and use in source and binary forms, with or without
 * modification, are permitted provided that the following conditions are met:
 *     * Redistributions of source code must retain the above copyright
 *       notice, this list of conditions and the following disclaimer.
 *     * Redistributions in binary form must reproduce the above copyright
 *       notice, this list of conditions and the following disclaimer in the
 *       documentation and/or other materials provided with the distribution.
 *     * Neither the name of the OpenSimulator Project nor the
 *       names of its contributors may be used to endorse or promote products
 *       derived from this software without specific prior written permission.
 *
 * THIS SOFTWARE IS PROVIDED BY THE DEVELOPERS ``AS IS'' AND ANY
 * EXPRESS OR IMPLIED WARRANTIES, INCLUDING, BUT NOT LIMITED TO, THE IMPLIED
 * WARRANTIES OF MERCHANTABILITY AND FITNESS FOR A PARTICULAR PURPOSE ARE
 * DISCLAIMED. IN NO EVENT SHALL THE CONTRIBUTORS BE LIABLE FOR ANY
 * DIRECT, INDIRECT, INCIDENTAL, SPECIAL, EXEMPLARY, OR CONSEQUENTIAL DAMAGES
 * (INCLUDING, BUT NOT LIMITED TO, PROCUREMENT OF SUBSTITUTE GOODS OR SERVICES;
 * LOSS OF USE, DATA, OR PROFITS; OR BUSINESS INTERRUPTION) HOWEVER CAUSED AND
 * ON ANY THEORY OF LIABILITY, WHETHER IN CONTRACT, STRICT LIABILITY, OR TORT
 * (INCLUDING NEGLIGENCE OR OTHERWISE) ARISING IN ANY WAY OUT OF THE USE OF THIS
 * SOFTWARE, EVEN IF ADVISED OF THE POSSIBILITY OF SUCH DAMAGE.
 */

using System;
using System.Collections.Generic;
using System.Reflection;
using log4net;
using Nini.Config;
using OpenSim.Region.Framework.Interfaces;
using OpenSim.Region.Framework.Scenes;
using OpenSim.Server.Base;
using OpenSim.Services.Interfaces;

using OpenMetaverse;

namespace OpenSim.Region.CoreModules.ServiceConnectorsOut.UserAccounts
{
    [RegionModule("LocalUserAccountServicesConnector")]
    public class LocalUserAccountServicesConnector : ISharedRegionModule, IUserAccountService
    {
        private static readonly ILog m_log =
                LogManager.GetLogger(
                MethodBase.GetCurrentMethod().DeclaringType);

        private IUserAccountService m_UserService;
        private UserAccountCache m_Cache;

        private bool m_Enabled = false;

        #region ISharedRegionModule

        public Type ReplaceableInterface 
        {
            get { return null; }
        }

        public string Name
        {
            get { return "LocalUserAccountServicesConnector"; }
        }

        public void Initialise(IConfigSource source)
        {
            IConfig moduleConfig = source.Configs["Modules"];
            if (moduleConfig != null)
            {
                string name = moduleConfig.GetString("UserAccountServices", "");
                if (name == Name)
                {
                    IConfig userConfig = source.Configs["UserAccountService"];
                    if (userConfig == null)
                    {
                        m_log.Error("[LOCAL USER ACCOUNT SERVICE CONNECTOR]: UserAccountService missing from OpenSim.ini");
                        return;
                    }

                    string serviceDll = userConfig.GetString("LocalServiceModule", String.Empty);

                    if (serviceDll == String.Empty)
                    {
                        m_log.Error("[LOCAL USER ACCOUNT SERVICE CONNECTOR]: No LocalServiceModule named in section UserService");
                        return;
                    }

                    Object[] args = new Object[] { source };
                    m_UserService = ServerUtils.LoadPlugin<IUserAccountService>(serviceDll, args);

                    if (m_UserService == null)
                    {
                        m_log.ErrorFormat(
                            "[LOCAL USER ACCOUNT SERVICE CONNECTOR]: Cannot load user account service specified as {0}", serviceDll);
                        return;
                    }
                    m_Enabled = true;
                    m_Cache = new UserAccountCache();

                    m_log.Info("[LOCAL USER ACCOUNT SERVICE CONNECTOR]: Local user connector enabled");
                }
            }
        }

        public void PostInitialise()
        {
            if (!m_Enabled)
                return;
        }

        public void Close()
        {
            if (!m_Enabled)
                return;
        }

        public void AddRegion(Scene scene)
        {
            if (!m_Enabled)
                return;

            scene.RegisterModuleInterface<IUserAccountService>(m_UserService);
        }

        public void RemoveRegion(Scene scene)
        {
            if (!m_Enabled)
                return;
        }

        public void RegionLoaded(Scene scene)
        {
            if (!m_Enabled)
                return;

            m_log.InfoFormat("[LOCAL USER ACCOUNT SERVICE CONNECTOR]: Enabled local user accounts for region {0}", scene.RegionInfo.RegionName);
        }

        #endregion

        #region IUserAccountService

        public UserAccount GetUserAccount(UUID scopeID, UUID userID)
        {
            bool inCache = false;
            UserAccount account = m_Cache.Get(userID, out inCache);
            if (inCache)
                return account;

            account = m_UserService.GetUserAccount(scopeID, userID);
            m_Cache.Cache(userID, account);

            return account;
        }

        public UserAccount GetUserAccount(UUID scopeID, string firstName, string lastName)
        {
            bool inCache = false;
            UserAccount account = m_Cache.Get(firstName + " " + lastName, out inCache);
            if (inCache)
                return account;

            account = m_UserService.GetUserAccount(scopeID, firstName, lastName);
            if (account != null)
                m_Cache.Cache(account.PrincipalID, account);

            return account;
        }

        public UserAccount GetUserAccount(UUID scopeID, string Email)
        {
            return m_UserService.GetUserAccount(scopeID, Email);
        }

        public List<UserAccount> GetUserAccounts(UUID scopeID, string query)
        {
            return m_UserService.GetUserAccounts(scopeID, query);
        }

        // Update all updatable fields
        //
        public bool StoreUserAccount(UserAccount data)
        {
            return m_UserService.StoreUserAccount(data);
        }

        #endregion

    }
}
