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
using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.Xml;
using log4net;
using OpenMetaverse;
using OpenSim.Framework;
using OpenSim.Framework.Serialization;
using OpenSim.Framework.Serialization.External;
using OpenSim.Framework.Communications;
using OpenSim.Framework.Communications.Osp;
using OpenSim.Region.CoreModules.World.Archiver;
using OpenSim.Region.Framework.Scenes;
using OpenSim.Services.Interfaces;

namespace OpenSim.Region.CoreModules.Avatar.Inventory.Archiver
{
    public class InventoryArchiveWriteRequest
    {
        private static readonly ILog m_log = LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);

        /// <value>
        /// Used to select all inventory nodes in a folder but not the folder itself
        /// </value>
        private const string STAR_WILDCARD = "*";

        private InventoryArchiverModule m_module;
        private UserAccount m_userInfo;
        private string m_invPath;
        protected TarArchiveWriter m_archiveWriter;
        protected UuidGatherer m_assetGatherer;

        /// <value>
        /// We only use this to request modules
        /// </value>
        protected Scene m_scene;

        /// <value>
        /// ID of this request
        /// </value>
        protected Guid m_id;

        /// <value>
        /// Used to collect the uuids of the assets that we need to save into the archive
        /// </value>
        protected Dictionary<UUID, AssetType> m_assetUuids = new Dictionary<UUID, AssetType>();

        /// <value>
        /// Used to collect the uuids of the users that we need to save into the archive
        /// </value>
        protected Dictionary<UUID, int> m_userUuids = new Dictionary<UUID, int>();

        /// <value>
        /// The stream to which the inventory archive will be saved.
        /// </value>
        private Stream m_saveStream;

        /// <summary>
        /// Constructor
        /// </summary>
        public InventoryArchiveWriteRequest(
            Guid id, InventoryArchiverModule module, Scene scene, 
            UserAccount userInfo, string invPath, string savePath)
            : this(
                id,
                module,
                scene,
                userInfo,
                invPath,
                new GZipStream(new FileStream(savePath, FileMode.Create), CompressionMode.Compress))
        {
        }

        /// <summary>
        /// Constructor
        /// </summary>
        public InventoryArchiveWriteRequest(
            Guid id, InventoryArchiverModule module, Scene scene, 
            UserAccount userInfo, string invPath, Stream saveStream)
        {
            m_id = id;
            m_module = module;
            m_scene = scene;
            m_userInfo = userInfo;
            m_invPath = invPath;
            m_saveStream = saveStream;
            m_assetGatherer = new UuidGatherer(m_scene.AssetService);
        }

        protected void ReceivedAllAssets(ICollection<UUID> assetsFoundUuids, ICollection<UUID> assetsNotFoundUuids)
        {
            Exception reportedException = null;
            bool succeeded = true;
             
            try
            {
                m_archiveWriter.Close();
            }
            catch (Exception e)
            {
                reportedException = e;
                succeeded = false;
            }
            finally
            {
                m_saveStream.Close();
            }

            m_module.TriggerInventoryArchiveSaved(
                m_id, succeeded, m_userInfo, m_invPath, m_saveStream, reportedException);
        }

        protected void SaveInvItem(InventoryItemBase inventoryItem, string path)
        {

            string filename = path + CreateArchiveItemName(inventoryItem);

            // Record the creator of this item for user record purposes (which might go away soon)
            m_userUuids[inventoryItem.CreatorIdAsUuid] = 1;

            InventoryItemBase saveItem = (InventoryItemBase)inventoryItem.Clone();
            saveItem.CreatorId = OspResolver.MakeOspa(saveItem.CreatorIdAsUuid, m_scene.UserAccountService);

            string serialization = UserInventoryItemSerializer.Serialize(saveItem);
            m_archiveWriter.WriteFile(filename, serialization);

            m_assetGatherer.GatherAssetUuids(saveItem.AssetID, (AssetType)saveItem.AssetType, m_assetUuids);
        }

        /// <summary>
        /// Save an inventory folder
        /// </summary>
        /// <param name="inventoryFolder">The inventory folder to save</param>
        /// <param name="path">The path to which the folder should be saved</param>
        /// <param name="saveThisFolderItself">If true, save this folder itself.  If false, only saves contents</param>
        protected void SaveInvFolder(InventoryFolderBase inventoryFolder, string path, bool saveThisFolderItself)
        {

            if (saveThisFolderItself)
            {
                path += CreateArchiveFolderName(inventoryFolder);

                // We need to make sure that we record empty folders
                m_archiveWriter.WriteDir(path);
            }

            InventoryCollection contents 
                = m_scene.InventoryService.GetFolderContent(inventoryFolder.Owner, inventoryFolder.ID);

            foreach (InventoryFolderBase childFolder in contents.Folders)
            {
                SaveInvFolder(childFolder, path, true);
            }

            foreach (InventoryItemBase item in contents.Items)
            {
                SaveInvItem(item, path);
            }
        }

        /// <summary>
        /// Execute the inventory write request
        /// </summary>
        public void Execute()
        {
            try
            {
                InventoryFolderBase inventoryFolder = null;
                InventoryItemBase inventoryItem = null;
                InventoryFolderBase rootFolder = m_scene.InventoryService.GetRootFolder(m_userInfo.PrincipalID);
    
                bool saveFolderContentsOnly = false;
    
                // Eliminate double slashes and any leading / on the path.
                string[] components
                    = m_invPath.Split(
                        new string[] { InventoryFolderImpl.PATH_DELIMITER }, StringSplitOptions.RemoveEmptyEntries);
    
                int maxComponentIndex = components.Length - 1;
    
                // If the path terminates with a STAR then later on we want to archive all nodes in the folder but not the
                // folder itself.  This may get more sophisicated later on
                if (maxComponentIndex >= 0 && components[maxComponentIndex] == STAR_WILDCARD)
                {
                    saveFolderContentsOnly = true;
                    maxComponentIndex--;
                }
    
                m_invPath = String.Empty;
                for (int i = 0; i <= maxComponentIndex; i++)
                {
                    m_invPath += components[i] + InventoryFolderImpl.PATH_DELIMITER;
                }
    
                // Annoyingly Split actually returns the original string if the input string consists only of delimiters
                // Therefore if we still start with a / after the split, then we need the root folder
                if (m_invPath.Length == 0)
                {
                    inventoryFolder = rootFolder;
                }
                else
                {
                    m_invPath = m_invPath.Remove(m_invPath.LastIndexOf(InventoryFolderImpl.PATH_DELIMITER));
                    List<InventoryFolderBase> candidateFolders 
                        = InventoryArchiveUtils.FindFolderByPath(m_scene.InventoryService, rootFolder, m_invPath);
                    if (candidateFolders.Count > 0)
                        inventoryFolder = candidateFolders[0];
                }
    
                // The path may point to an item instead
                if (inventoryFolder == null)
                {
                    inventoryItem = InventoryArchiveUtils.FindItemByPath(m_scene.InventoryService, rootFolder, m_invPath);
                    //inventoryItem = m_userInfo.RootFolder.FindItemByPath(m_invPath);
                }
    
                if (null == inventoryFolder && null == inventoryItem)
                {
                    // We couldn't find the path indicated 
                    string errorMessage = string.Format("Aborted save.  Could not find inventory path {0}", m_invPath);
                    Exception e = new InventoryArchiverException(errorMessage);
                    m_module.TriggerInventoryArchiveSaved(m_id, false, m_userInfo, m_invPath, m_saveStream, e);
                    throw e;
                }
            
                m_archiveWriter = new TarArchiveWriter(m_saveStream);

                // Write out control file.  This has to be done first so that subsequent loaders will see this file first
                // XXX: I know this is a weak way of doing it since external non-OAR aware tar executables will not do this
                m_archiveWriter.WriteFile(ArchiveConstants.CONTROL_FILE_PATH, Create0p1ControlFile());
                m_log.InfoFormat("[INVENTORY ARCHIVER]: Added control file to archive.");
                
                if (inventoryFolder != null)
                {
                    m_log.DebugFormat(
                        "[INVENTORY ARCHIVER]: Found folder {0} {1} at {2}",
                        inventoryFolder.Name, 
                        inventoryFolder.ID, 
                        m_invPath == String.Empty ? InventoryFolderImpl.PATH_DELIMITER : m_invPath);
    
                    //recurse through all dirs getting dirs and files
                    SaveInvFolder(inventoryFolder, ArchiveConstants.INVENTORY_PATH, !saveFolderContentsOnly);
                }
                else if (inventoryItem != null)
                {
                    m_log.DebugFormat(
                        "[INVENTORY ARCHIVER]: Found item {0} {1} at {2}",
                        inventoryItem.Name, inventoryItem.ID, m_invPath);
    
                    SaveInvItem(inventoryItem, ArchiveConstants.INVENTORY_PATH);
                }
            
                // Don't put all this profile information into the archive right now.
                //SaveUsers();
                            
                new AssetsRequest(
                    new AssetsArchiver(m_archiveWriter), m_assetUuids, m_scene.AssetService, ReceivedAllAssets).Execute();
            }
            catch (Exception)
            {
                m_saveStream.Close();
                throw;
            }
        }

        /// <summary>
        /// Save information for the users that we've collected.
        /// </summary>
        protected void SaveUsers()
        {
            m_log.InfoFormat("[INVENTORY ARCHIVER]: Saving user information for {0} users", m_userUuids.Count);

            foreach (UUID creatorId in m_userUuids.Keys)
            {
                // Record the creator of this item
                UserAccount creator = m_scene.UserAccountService.GetUserAccount(m_scene.RegionInfo.ScopeID, creatorId);

                if (creator != null)
                {
                    m_archiveWriter.WriteFile(
                        ArchiveConstants.USERS_PATH + creator.FirstName + " " + creator.LastName + ".xml",
                        UserProfileSerializer.Serialize(creator.PrincipalID, creator.FirstName, creator.LastName));
                }
                else
                {
                    m_log.WarnFormat("[INVENTORY ARCHIVER]: Failed to get creator profile for {0}", creatorId);
                }
            }
        }

        /// <summary>
        /// Create the archive name for a particular folder.
        /// </summary>
        ///
        /// These names are prepended with an inventory folder's UUID so that more than one folder can have the
        /// same name
        /// 
        /// <param name="folder"></param>
        /// <returns></returns>
        public static string CreateArchiveFolderName(InventoryFolderBase folder)
        {
            return CreateArchiveFolderName(folder.Name, folder.ID);
        }

        /// <summary>
        /// Create the archive name for a particular item.
        /// </summary>
        ///
        /// These names are prepended with an inventory item's UUID so that more than one item can have the
        /// same name
        /// 
        /// <param name="item"></param>
        /// <returns></returns>
        public static string CreateArchiveItemName(InventoryItemBase item)
        {
            return CreateArchiveItemName(item.Name, item.ID);
        }

        /// <summary>
        /// Create an archive folder name given its constituent components
        /// </summary>
        /// <param name="name"></param>
        /// <param name="id"></param>
        /// <returns></returns>
        public static string CreateArchiveFolderName(string name, UUID id)
        {
            return string.Format(
                "{0}{1}{2}/",
                InventoryArchiveUtils.EscapeArchivePath(name),
                ArchiveConstants.INVENTORY_NODE_NAME_COMPONENT_SEPARATOR,
                id);
        }

        /// <summary>
        /// Create an archive item name given its constituent components
        /// </summary>
        /// <param name="name"></param>
        /// <param name="id"></param>
        /// <returns></returns>
        public static string CreateArchiveItemName(string name, UUID id)
        {
            return string.Format(
                "{0}{1}{2}.xml",
                InventoryArchiveUtils.EscapeArchivePath(name),
                ArchiveConstants.INVENTORY_NODE_NAME_COMPONENT_SEPARATOR,
                id);
        }

        /// <summary>
        /// Create the control file for a 0.1 version archive
        /// </summary>
        /// <returns></returns>
        public static string Create0p1ControlFile()
        {
            int majorVersion = 0, minorVersion = 1;
            
            m_log.InfoFormat("[INVENTORY ARCHIVER]: Creating version {0}.{1} IAR", majorVersion, minorVersion);
            
            StringWriter sw = new StringWriter();
            XmlTextWriter xtw = new XmlTextWriter(sw);
            xtw.Formatting = Formatting.Indented;
            xtw.WriteStartDocument();
            xtw.WriteStartElement("archive");
            xtw.WriteAttributeString("major_version", majorVersion.ToString());
            xtw.WriteAttributeString("minor_version", minorVersion.ToString());
            xtw.WriteEndElement();

            xtw.Flush();
            xtw.Close();

            String s = sw.ToString();
            sw.Close();

            return s;
        }
    }
}