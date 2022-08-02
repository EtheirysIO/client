﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using Dalamud.Utility;
using FFXIVClientStructs.FFXIV.Client.Game.Character;
using FFXIVClientStructs.FFXIV.Client.Graphics.Scene;
using FFXIVClientStructs.FFXIV.Client.System.Resource;
using EtheirysSynchronos.API;
using EtheirysSynchronos.Interop;
using EtheirysSynchronos.Managers;
using EtheirysSynchronos.Models;
using EtheirysSynchronos.Utils;
using Penumbra.GameData.ByteString;
using Penumbra.Interop.Structs;
using Object = FFXIVClientStructs.FFXIV.Client.Graphics.Scene.Object;

namespace EtheirysSynchronos.Factories;

public class CharacterDataFactory
{
    private readonly DalamudUtil _dalamudUtil;
    private readonly IpcManager _ipcManager;

    public CharacterDataFactory(DalamudUtil dalamudUtil, IpcManager ipcManager)
    {
        Logger.Verbose("Creating " + nameof(CharacterDataFactory));

        _dalamudUtil = dalamudUtil;
        _ipcManager = ipcManager;
    }

    public CharacterData BuildCharacterData(CharacterData previousData, ObjectKind objectKind, IntPtr playerPointer, CancellationToken token)
    {
        if (!_ipcManager.Initialized)
        {
            throw new ArgumentException("Penumbra is not connected");
        }

        if (playerPointer == IntPtr.Zero)
        {
            Logger.Verbose("Pointer was zero for " + objectKind);
            previousData.FileReplacements.Remove(objectKind);
            previousData.GlamourerString.Remove(objectKind);
            return previousData;
        }

        var previousFileReplacements = previousData.FileReplacements.ToDictionary(d => d.Key, d => d.Value);
        var previousGlamourerData = previousData.GlamourerString.ToDictionary(d => d.Key, d => d.Value);

        try
        {
            return CreateCharacterData(previousData, objectKind, playerPointer, token);
        }
        catch (OperationCanceledException)
        {
            Logger.Debug("Cancelled creating Character data");
        }
        catch (Exception e)
        {
            Logger.Warn("Failed to create " + objectKind + " data");
            Logger.Warn(e.Message);
            Logger.Warn(e.StackTrace ?? string.Empty);
        }

        previousData.FileReplacements = previousFileReplacements;
        previousData.GlamourerString = previousGlamourerData;
        return previousData;
    }

    private (string, string) GetIndentationForInheritanceLevel(int inheritanceLevel)
    {
        return (string.Join("", Enumerable.Repeat("\t", inheritanceLevel)), string.Join("", Enumerable.Repeat("\t", inheritanceLevel + 2)));
    }

    private void DebugPrint(FileReplacement fileReplacement, ObjectKind objectKind, string resourceType, int inheritanceLevel)
    {
        var indentation = GetIndentationForInheritanceLevel(inheritanceLevel);

        if (fileReplacement.HasFileReplacement)
        {
            Logger.Verbose(indentation.Item1 + objectKind + resourceType + " [" + string.Join(", ", fileReplacement.GamePaths) + "]");
            Logger.Verbose(indentation.Item2 + "=> " + fileReplacement.ResolvedPath);
        }
    }

    private unsafe void AddReplacementsFromRenderModel(RenderModel* mdl, ObjectKind objectKind, CharacterData cache, int inheritanceLevel = 0)
    {
        if (mdl == null || mdl->ResourceHandle == null || mdl->ResourceHandle->Category != ResourceCategory.Chara)
        {
            return;
        }

        string mdlPath;
        try
        {
            mdlPath = new Utf8String(mdl->ResourceHandle->FileName()).ToString();
        }
        catch
        {
            Logger.Warn("Could not get model data for " + objectKind);
            return;
        }
        //Logger.Verbose("Adding File Replacement for Model " + mdlPath);

        FileReplacement mdlFileReplacement = CreateFileReplacement(mdlPath);
        DebugPrint(mdlFileReplacement, objectKind, "Model", inheritanceLevel);

        cache.AddFileReplacement(objectKind, mdlFileReplacement);

        for (var mtrlIdx = 0; mtrlIdx < mdl->MaterialCount; mtrlIdx++)
        {
            var mtrl = (Material*)mdl->Materials[mtrlIdx];
            if (mtrl == null) continue;

            AddReplacementsFromMaterial(mtrl, objectKind, cache, inheritanceLevel + 1);
        }
    }

    private unsafe void AddReplacementsFromMaterial(Material* mtrl, ObjectKind objectKind, CharacterData cache, int inheritanceLevel = 0)
    {
        string fileName;
        try
        {
            fileName = new Utf8String(mtrl->ResourceHandle->FileName()).ToString();

        }
        catch
        {
            Logger.Warn("Could not get material data for " + objectKind);
            return;
        }

        //Logger.Verbose("Adding File Replacement for Material " + fileName);
        var mtrlPath = fileName.Split("|")[2];

        if (cache.FileReplacements.ContainsKey(objectKind))
        {
            if (cache.FileReplacements[objectKind].Any(c => c.ResolvedPath.Contains(mtrlPath)))
            {
                return;
            }
        }

        var mtrlFileReplacement = CreateFileReplacement(mtrlPath);
        DebugPrint(mtrlFileReplacement, objectKind, "Material", inheritanceLevel);

        cache.AddFileReplacement(objectKind, mtrlFileReplacement);

        var mtrlResourceHandle = (MtrlResource*)mtrl->ResourceHandle;
        for (var resIdx = 0; resIdx < mtrlResourceHandle->NumTex; resIdx++)
        {
            string? texPath = null;
            try
            {
                texPath = new Utf8String(mtrlResourceHandle->TexString(resIdx)).ToString();
            }
            catch
            {
                Logger.Warn("Could not get Texture data for Material " + fileName);
            }

            if (string.IsNullOrEmpty(texPath)) continue;

            AddReplacementsFromTexture(texPath, objectKind, cache, inheritanceLevel + 1);
        }
    }

    private void AddReplacementsFromTexture(string texPath, ObjectKind objectKind, CharacterData cache, int inheritanceLevel = 0, bool doNotReverseResolve = true)
    {
        if (texPath.IsNullOrEmpty()) return;

        //Logger.Verbose("Adding File Replacement for Texture " + texPath);

        if (cache.FileReplacements.ContainsKey(objectKind))
        {
            if (cache.FileReplacements[objectKind].Any(c => c.GamePaths.Contains(texPath)))
            {
                return;
            }
        }

        var texFileReplacement = CreateFileReplacement(texPath, doNotReverseResolve);
        DebugPrint(texFileReplacement, objectKind, "Texture", inheritanceLevel);

        cache.AddFileReplacement(objectKind, texFileReplacement);

        if (texPath.Contains("/--")) return;

        var texDx11Replacement =
            CreateFileReplacement(texPath.Insert(texPath.LastIndexOf('/') + 1, "--"), doNotReverseResolve);

        DebugPrint(texDx11Replacement, objectKind, "Texture (DX11)", inheritanceLevel);

        cache.AddFileReplacement(objectKind, texDx11Replacement);
    }

    private unsafe CharacterData CreateCharacterData(CharacterData previousData, ObjectKind objectKind, IntPtr charaPointer, CancellationToken token)
    {
        Stopwatch st = Stopwatch.StartNew();
        var chara = _dalamudUtil.CreateGameObject(charaPointer)!;
        while (!_dalamudUtil.IsObjectPresent(chara))
        {
            Logger.Verbose("Character is null but it shouldn't be, waiting");
            Thread.Sleep(50);
        }
        _dalamudUtil.WaitWhileCharacterIsDrawing(charaPointer);

        if (previousData.FileReplacements.ContainsKey(objectKind))
        {
            previousData.FileReplacements[objectKind].Clear();
        }

        previousData.ManipulationString = _ipcManager.PenumbraGetMetaManipulations();

        if (objectKind is not ObjectKind.Mount)
        {
            previousData.GlamourerString[objectKind] = _ipcManager.GlamourerGetCharacterCustomization(chara);
        }

        var human = (Human*)((Character*)charaPointer)->GameObject.GetDrawObject();
        for (var mdlIdx = 0; mdlIdx < human->CharacterBase.SlotCount; ++mdlIdx)
        {
            var mdl = (RenderModel*)human->CharacterBase.ModelArray[mdlIdx];
            if (mdl == null || mdl->ResourceHandle == null || mdl->ResourceHandle->Category != ResourceCategory.Chara)
            {
                continue;
            }

            token.ThrowIfCancellationRequested();

            AddReplacementsFromRenderModel(mdl, objectKind, previousData, 0);
        }

        if (objectKind == ObjectKind.Player)
        {
            var weaponObject = (Weapon*)((Object*)human)->ChildObject;

            if ((IntPtr)weaponObject != IntPtr.Zero)
            {
                var mainHandWeapon = weaponObject->WeaponRenderModel->RenderModel;

                AddReplacementsFromRenderModel(mainHandWeapon, objectKind, previousData, 0);

                if (weaponObject->NextSibling != (IntPtr)weaponObject)
                {
                    var offHandWeapon = ((Weapon*)weaponObject->NextSibling)->WeaponRenderModel->RenderModel;

                    AddReplacementsFromRenderModel(offHandWeapon, objectKind, previousData, 1);
                }
            }

            AddReplacementSkeleton(((HumanExt*)human)->Human.RaceSexId, objectKind, previousData);
            try
            {
                AddReplacementsFromTexture(new Utf8String(((HumanExt*)human)->Decal->FileName()).ToString(), objectKind, previousData, 0, false);
            }
            catch
            {
                Logger.Warn("Could not get Decal data");
            }
            try
            {
                AddReplacementsFromTexture(new Utf8String(((HumanExt*)human)->LegacyBodyDecal->FileName()).ToString(), objectKind, previousData, 0, false);
            }
            catch
            {
                Logger.Warn("Could not get Legacy Body Decal Data");
            }
        }

        st.Stop();
        Logger.Verbose("Building " + objectKind + " Data took " + st.Elapsed);

        return previousData;
    }

    private void AddReplacementSkeleton(ushort raceSexId, ObjectKind objectKind, CharacterData cache)
    {
        string raceSexIdString = raceSexId.ToString("0000");

        string skeletonPath = $"chara/human/c{raceSexIdString}/skeleton/base/b0001/skl_c{raceSexIdString}b0001.sklb";

        //Logger.Verbose("Adding File Replacement for Skeleton " + skeletonPath);

        var replacement = CreateFileReplacement(skeletonPath, true);
        cache.AddFileReplacement(objectKind, replacement);

        DebugPrint(replacement, objectKind, "SKLB", 0);
    }

    private FileReplacement CreateFileReplacement(string path, bool doNotReverseResolve = false)
    {
        var fileReplacement = new FileReplacement(_ipcManager.PenumbraModDirectory()!);
        if (!doNotReverseResolve)
        {
            fileReplacement.GamePaths =
                _ipcManager.PenumbraReverseResolvePlayer(path).ToList();
            fileReplacement.SetResolvedPath(path);
        }
        else
        {
            fileReplacement.GamePaths = new List<string> { path };
            fileReplacement.SetResolvedPath(_ipcManager.PenumbraResolvePath(path)!);
        }

        return fileReplacement;
    }
}
