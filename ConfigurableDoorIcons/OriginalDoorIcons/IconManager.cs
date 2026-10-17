using HarmonyLib;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using UnityEngine;

namespace DoorIcons
{
    public static class IconManager
    {
        private static readonly AccessTools.FieldRef<AccessControl, List<KeyValuePair<int, AccessControl.Permission>>> GetSavedPermissions = AccessTools.FieldRefAccess<AccessControl, List<KeyValuePair<int, AccessControl.Permission>>>("savedPermissionsById");

        private static readonly AccessTools.FieldRef<Door, Door.ControlState> GetDoorStatus = AccessTools.FieldRefAccess<Door, Door.ControlState>("controlState");

        private sealed class PermissionIconRow
        {
            public PermissionIconRow(
                GameObject gameObject,
                SpriteRenderer portraitRenderer,
                SpriteRenderer permissionRenderer)
            {
                GameObject = gameObject;
                PortraitRenderer = portraitRenderer;
                PermissionRenderer = permissionRenderer;
            }

            public GameObject GameObject { get; }
            public SpriteRenderer PortraitRenderer { get; }
            public SpriteRenderer PermissionRenderer { get; }
            public CrewPortrait? CrewPortrait { get; set; }
            public UnityEngine.UI.Image? RobotPortraitImage { get; set; }
        }

        private sealed class PermissionIconHoverUpdater : MonoBehaviour
        {
            private Door? door;

            public void Initialize(Door targetDoor)
            {
                door = targetDoor;
            }

            private void Update()
            {
                if (door != null)
                {
                    UpdatePermissionIconScale(door);
                }
            }
        }

        private sealed class IndividualPermission
        {
            public IndividualPermission(
                Tag model,
                ExtendedDoorState permission,
                IAssignableIdentity? identity,
                Tag? robotModelTag = null)
            {
                Model = model;
                Permission = permission;
                Identity = identity;
                RobotModelTag = robotModelTag;
            }

            public Tag Model { get; }
            public ExtendedDoorState Permission { get; }
            public IAssignableIdentity? Identity { get; }
            public Tag? RobotModelTag { get; }
        }

        private static readonly Dictionary<Door, List<PermissionIconRow>> PermissionIcons = new Dictionary<Door, List<PermissionIconRow>>();

        private static readonly HashSet<string> LoggedDiagnostics = new HashSet<string>();

        public static GameObject CreateIcon(Door door)
        {
            var go = new GameObject("DoorIcon");
            var renderer = go.FindOrAddComponent<SpriteRenderer>();

            renderer.material.renderQueue = 5000;

            // Apply the user's preferred transparency
            renderer.color = renderer.color with { a = ConfigurableDoorIcons.Options.Instance.Transparency };

            Util.KInstantiate(renderer, GameScreenManager.Instance.worldSpaceCanvas);

            go.transform.localScale = new Vector3
            (
                0.005f,
                0.005f,
                1
            );

            var pos = Grid.PosToXY(door.transform.position);

            var anchorX = pos.X + 0.5f;
            var anchorY = pos.Y + 0.5f;

            var def = door.building.Def;

            var width = def.WidthInCells;
            var height = def.HeightInCells;

            var rotatable = door.GetComponent<Rotatable>();

            go.transform.position = new Vector3
            (
                anchorX + ((width - 1) / 2f),
                anchorY + ((height - 1) / 2f),
                Grid.GetLayerZ(Grid.SceneLayer.SceneMAX)
            );

            if (rotatable == null)
            {
                Debug.LogWarning("[ConfigurableDoorIcons] Vanilla doors usually have Rotatable component");

                return go;
            }

            switch (rotatable.GetOrientation())
            {
                case Orientation.R90:
                    go.transform.position = new Vector3
                    (
                        anchorX + ((height - 1) / 2f),
                        anchorY + ((width - 1) / 2f),
                        Grid.GetLayerZ(Grid.SceneLayer.SceneMAX)
                    );

                    renderer.transform.Rotate(0, 0, -90);

                    break;
                case Orientation.R180:
                    go.transform.position = new Vector3
                    (
                        anchorX - ((width - 1) / 2f),
                        anchorY - ((height - 1) / 2f),
                        Grid.GetLayerZ(Grid.SceneLayer.SceneMAX)
                    );

                    break;
                case Orientation.R270:
                    go.transform.position = new Vector3
                    (
                        anchorX - ((height - 1) / 2f),
                        anchorY - ((width - 1) / 2f),
                        Grid.GetLayerZ(Grid.SceneLayer.SceneMAX)
                    );

                    renderer.transform.Rotate(0, 0, -90);

                    break;
            }

            // Need to add the game object to DoorIcons in order for UpdateIcon/SetIcon to work.
            State.DoorIcons.Add
            (
                door,
                go
            );

            UpdateIcon(door);

            return go;
        }

        public static void UpdateIcon(Door door)
        {
            ExtendedDoorState state = GetExtendedDoorState(door);
            SetIcon(door, state);
        }

        public static ExtendedDoorState GetExtendedDoorState(Door door)
        {
            var logicPorts = door.GetComponent<LogicPorts>();
            var accessControl = door.GetComponent<AccessControl>();

            if (accessControl == null)
            {
                return ExtendedDoorState.Invalid;
            }

            if (logicPorts != null && logicPorts.IsPortConnected(Door.OPEN_CLOSE_PORT_ID))
            {
                return ExtendedDoorState.Automation;
            }

            var modelPermissions = GetModelPermissions(accessControl);
            var hasCustomDupePermissions = HasCustomDupePermissions(accessControl);
            var hasMixedModelPermissions = HasCustomGlobalPermissions(modelPermissions, out _);
            switch (GetDoorStatus(door))
            {
                case Door.ControlState.Auto:
                    if (hasCustomDupePermissions)
                    {
                        return ExtendedDoorState.AccessCustom;
                    }

                    if (hasMixedModelPermissions)
                    {
                        return ExtendedDoorState.AccessCustom;
                    }

                    if (HasCustomGlobalPermissions(modelPermissions, out var permission) ||
                        (permission != ExtendedDoorState.Auto && permission != ExtendedDoorState.Invalid))
                    {
                        return permission;
                    }

                    return ExtendedDoorState.Auto;

                case Door.ControlState.Opened:
                    if (hasCustomDupePermissions)
                    {
                        return ExtendedDoorState.AccessCustom;
                    }

                    if (hasMixedModelPermissions)
                    {
                        return ExtendedDoorState.AccessCustom;
                    }

                    if (HasCustomGlobalPermissions(modelPermissions, out var permission2) ||
                        (permission2 != ExtendedDoorState.Auto && permission2 != ExtendedDoorState.Invalid))
                    {
                        return permission2;
                    }

                    return ExtendedDoorState.Open;

                case Door.ControlState.Locked:
                    return ExtendedDoorState.Locked;
            }

            return ExtendedDoorState.Invalid;
        }

        private static List<KeyValuePair<Tag, ExtendedDoorState>> GetModelPermissions(AccessControl access)
        {
            return GameTags.Minions.Models.AllModels
                .Concat([GameTags.Robot])
                .Select(tag => new KeyValuePair<Tag, ExtendedDoorState>(
                    tag,
                    GetExtendedPermission(access.GetDefaultPermission(tag))))
                .ToList();
        }

        private static ExtendedDoorState GetExtendedPermission(AccessControl.Permission permission)
        {
            return permission switch
            {
                AccessControl.Permission.Both => ExtendedDoorState.Auto,
                AccessControl.Permission.GoLeft => ExtendedDoorState.AccessLeft,
                AccessControl.Permission.GoRight => ExtendedDoorState.AccessRight,
                AccessControl.Permission.Neither => ExtendedDoorState.AccessRestricted,
                _ => ExtendedDoorState.Invalid,
            };
        }

        private static bool HasCustomGlobalPermissions(
            List<KeyValuePair<Tag, ExtendedDoorState>> modelPermissions,
            out ExtendedDoorState doorState)
        {
            doorState = ExtendedDoorState.Invalid;
            var setDoorState = false;
            foreach (var modelPermission in modelPermissions)
            {
                if (!setDoorState)
                {
                    // First permission we encounter in the list
                    doorState = modelPermission.Value;
                    setDoorState = true;
                }
                else if (doorState != modelPermission.Value)
                {
                    // If ever we encounter different permissions in the list, return "custom"
                    doorState = ExtendedDoorState.AccessCustom;
                    return true;
                }
            }

            return false;
        }

        private static bool HasCustomDupePermissions(AccessControl access)
        {
            return GetCustomDupePermissions(access).Count > 0;
        }

        private static List<IndividualPermission> GetCustomDupePermissions(AccessControl access)
        {
            var savedPermissions = GetSavedPermissions(access)
                .ToDictionary(savedPermission => savedPermission.Key, savedPermission => savedPermission.Value);
            var permissions = new List<IndividualPermission>();
            foreach (var identity in Components.MinionAssignablesProxy.Items)
            {
                var prefabId = identity.GetComponent<KPrefabID>();
                if (prefabId == null || !savedPermissions.TryGetValue(prefabId.InstanceID, out var permission))
                {
                    continue;
                }

                savedPermissions.Remove(prefabId.InstanceID);
                var model = identity.GetMinionModel();
                AddCustomIndividualPermission(access, permissions, model, permission, identity);
            }

            var robotModelTags = GridRestrictionSerializer.Instance.ValidRobotTypes;
            foreach (var model in robotModelTags)
            {
                savedPermissions.Remove(GridRestrictionSerializer.Instance.GetTagId(model));
            }

            foreach (var (instanceId, permission) in savedPermissions)
            {
                var prefab = KPrefabIDTracker.Get()?.GetInstance(instanceId);
                IAssignableIdentity? identity = prefab != null
                    ? FindAssignableIdentity(prefab)
                    : null;
                if (identity == null)
                {
                    continue;
                }

                var model = identity is MinionAssignablesProxy minion
                    ? minion.GetMinionModel()
                    : GameTags.Robot;
                AddCustomIndividualPermission(access, permissions, model, permission, identity);
            }

            foreach (var model in GetVisibleRobotModelTags(robotModelTags))
            {
                AddCustomRobotModelPermission(access, permissions, model);
            }

            return permissions;
        }

        private static IEnumerable<Tag> GetVisibleRobotModelTags(IEnumerable<Tag> robotModelTags)
        {
            foreach (var model in robotModelTags)
            {
                if (model == GameTags.Robots.Models.FetchDrone &&
                    !Game.IsDlcActiveForCurrentSave("DLC3_ID"))
                {
                    continue;
                }

                if (model == GameTags.Robots.Models.ScoutRover &&
                    !Game.IsDlcActiveForCurrentSave("EXPANSION1_ID"))
                {
                    continue;
                }

                yield return model;
            }
        }

        private static void AddCustomRobotModelPermission(
            AccessControl access,
            List<IndividualPermission> permissions,
            Tag model)
        {
            var permission = access.GetSetPermission(model);
            var defaultPermission = access.GetDefaultPermission(GameTags.Robot);
            var isCustom = permission != defaultPermission;
            LogDiagnosticOnce(
                $"robot-permission:{access.GetInstanceID()}:{model.Name}:{defaultPermission}:{permission}",
                $"[ConfigurableDoorIcons] Robot access: model={model.Name}, tagId={GridRestrictionSerializer.Instance.GetTagId(model)}, robotDefault={defaultPermission}, set={permission}, custom={isCustom}");
            if (!isCustom)
            {
                return;
            }

            permissions.Add(new IndividualPermission(
                GameTags.Robot,
                GetExtendedPermission(permission),
                null,
                model));
        }

        private static void LogDiagnosticOnce(string key, string message)
        {
            if (LoggedDiagnostics.Add(key))
            {
                Debug.Log(message);
            }
        }

        private static IAssignableIdentity? FindAssignableIdentity(KPrefabID prefab)
        {
            var gameObject = prefab.gameObject;
            var proxy = gameObject.GetComponent<MinionAssignablesProxy>() ??
                        gameObject.GetComponentInParent<MinionAssignablesProxy>() ??
                        gameObject.GetComponentInChildren<MinionAssignablesProxy>(true);
            if (proxy != null)
            {
                return proxy;
            }

            return gameObject.GetComponentsInParent<MonoBehaviour>(true)
                .Concat(gameObject.GetComponentsInChildren<MonoBehaviour>(true))
                .OfType<IAssignableIdentity>()
                .FirstOrDefault();
        }

        private static void AddCustomIndividualPermission(
            AccessControl access,
            List<IndividualPermission> permissions,
            Tag model,
            AccessControl.Permission permission,
            IAssignableIdentity? identity)
        {
            if (permission == access.GetDefaultPermission(model))
            {
                return;
            }

            permissions.Add(new IndividualPermission(
                GetPermissionModelGroup(model),
                GetExtendedPermission(permission),
                identity));
        }

        private static List<PermissionDisplayEntry> GetPermissionDisplayEntries(
            List<KeyValuePair<Tag, ExtendedDoorState>> modelPermissions,
            List<IndividualPermission> individualPermissions)
        {
            var entries = new List<PermissionDisplayEntry>();
            var useGenericModel = modelPermissions.Count > 0 &&
                                  modelPermissions.All(permission =>
                                      permission.Value == modelPermissions[0].Value);
            if (useGenericModel)
            {
                entries.Add(new PermissionDisplayEntry(
                    modelPermissions[0].Key,
                    modelPermissions[0].Value,
                    useGenericPortrait: true));
            }

            foreach (var (model, permission) in modelPermissions)
            {
                if (!useGenericModel)
                {
                    entries.Add(new PermissionDisplayEntry(model, permission));
                }

                entries.AddRange(individualPermissions
                    .Where(individual => individual.Model == model)
                    .Select(individual => new PermissionDisplayEntry(
                        individual.Model,
                        individual.Permission,
                        individual.Identity,
                        individual.RobotModelTag)));
            }

            return entries;
        }

        private static Tag GetPermissionModelGroup(Tag model)
        {
            return GameTags.Minions.Models.AllModels.Contains(model) ? model : GameTags.Robot;
        }

        private sealed class PermissionDisplayEntry
        {
            public PermissionDisplayEntry(
                Tag model,
                ExtendedDoorState permission,
                IAssignableIdentity? identity = null,
                Tag? robotModelTag = null,
                bool useGenericPortrait = false)
            {
                Model = model;
                Permission = permission;
                Identity = identity;
                RobotModelTag = robotModelTag;
                UseGenericPortrait = useGenericPortrait;
            }

            public Tag Model { get; }
            public ExtendedDoorState Permission { get; }
            public IAssignableIdentity? Identity { get; }
            public Tag? RobotModelTag { get; }
            public bool UseGenericPortrait { get; }
        }

        private static void SetIcon(Door door, ExtendedDoorState targetState)
        {
            if (State.DoorIcons.TryGetValue(door, out var go))
            {
                var renderer = go.GetComponent<SpriteRenderer>();

                if (renderer != null)
                {
                    if ((targetState == ExtendedDoorState.Open &&
                         !ConfigurableDoorIcons.Options.Instance.ShowOnOpenDoors) ||
                        (targetState == ExtendedDoorState.Locked &&
                         !ConfigurableDoorIcons.Options.Instance.ShowOnLockedDoors))
                    {
                        DestroyPermissionIcons(door);
                        renderer.enabled = false;
                        return;
                    }

                    if (State.DoorSprites.TryGetValue(targetState, out var newSprite))
                    {
                        renderer.sprite = newSprite;
                    }
                    else
                    {
                        renderer.sprite = null;
                    }

                    var accessControl = door.GetComponent<AccessControl>();
                    var modelPermissions = accessControl != null
                        ? GetModelPermissions(accessControl)
                        : new List<KeyValuePair<Tag, ExtendedDoorState>>();
                    var individualPermissions = accessControl != null
                        ? GetCustomDupePermissions(accessControl)
                        : new List<IndividualPermission>();
                    var hasCustomPermissions = targetState != ExtendedDoorState.Automation &&
                                               accessControl != null &&
                                               (HasCustomGlobalPermissions(modelPermissions, out _) ||
                                                individualPermissions.Count > 0);

                    if (hasCustomPermissions)
                    {
                        renderer.enabled = false;
                        SetPermissionIcons(
                            door,
                            go.transform.position,
                            GetPermissionDisplayEntries(modelPermissions, individualPermissions));
                    }
                    else
                    {
                        DestroyPermissionIcons(door);
                        renderer.enabled = newSprite != null;
                    }
                }
            }
        }

        private static void SetPermissionIcons(
            Door door,
            Vector3 position,
            List<PermissionDisplayEntry> permissions)
        {
            if (!PermissionIcons.TryGetValue(door, out var rows) || rows.Count != permissions.Count)
            {
                DestroyPermissionIcons(door);
                rows = CreatePermissionIconRows(permissions.Count);
                PermissionIcons.Add(door, rows);
            }

            if (rows.Count > 0)
            {
                var hoverUpdater = rows[0].GameObject.GetComponent<PermissionIconHoverUpdater>() ??
                                   rows[0].GameObject.AddComponent<PermissionIconHoverUpdater>();
                hoverUpdater.Initialize(door);
            }

            var rowSpacing = 100f;
            var firstRowOffset = (permissions.Count - 1) * rowSpacing / 2f;
            var scale = GetPermissionIconScale(door, rows.Count, IsPointerOverDoor(door));
            var transparency = ConfigurableDoorIcons.Options.Instance.Transparency;

            for (var i = 0; i < permissions.Count; i++)
            {
                var entry = permissions[i];
                var row = rows[i];
                row.GameObject.transform.position = new Vector3(
                    position.x,
                    position.y + (firstRowOffset - i * rowSpacing) * 0.005f * scale,
                    position.z);
                row.GameObject.transform.localScale = new Vector3(
                    0.005f * scale,
                    0.005f * scale,
                    1f);
                if (entry.Identity != null)
                {
                    row.PortraitRenderer.enabled = false;
                    if (row.CrewPortrait == null)
                    {
                        row.CrewPortrait = Util.KInstantiateUI<CrewPortrait>(
                            Assets.UIPrefabs.TableScreenWidgets.MinionPortrait,
                            GameScreenManager.Instance.worldSpaceCanvas,
                            true);
                        row.CrewPortrait.useLabels = false;
                        row.CrewPortrait.transform.localScale = Vector3.one * 0.01f;
                        if (row.CrewPortrait.transform is RectTransform portraitRect)
                        {
                            portraitRect.anchorMin = portraitRect.anchorMax = new Vector2(0.5f, 0.5f);
                            portraitRect.pivot = new Vector2(0.5f, 0.5f);
                        }
                    }

                    row.CrewPortrait.gameObject.SetActive(true);
                    if (!ReferenceEquals(row.CrewPortrait.identityObject, entry.Identity))
                    {
                        row.CrewPortrait.SetIdentityObject(entry.Identity, false);
                    }

                    var portraitController = row.CrewPortrait.controller ??
                                             row.CrewPortrait.GetComponentInChildren<KBatchedAnimController>(true);
                    if (portraitController != null)
                    {
                        var portraitPosition = row.PortraitRenderer.bounds.center;
                        portraitController.transform.position = new Vector3(
                            portraitPosition.x - 0.03f,
                            portraitPosition.y - 0.165f,
                            portraitPosition.z);
                    }

                    row.CrewPortrait.SetAlpha(transparency);
                }
                else
                {
                    if (row.CrewPortrait != null)
                    {
                        row.CrewPortrait.gameObject.SetActive(false);
                    }

                    if (entry.RobotModelTag.HasValue)
                    {
                        var robotSprite = Def.GetUISprite(entry.RobotModelTag.Value, "ui", false).first;
                        row.PortraitRenderer.enabled = false;
                        if (robotSprite != null)
                        {
                            if (row.RobotPortraitImage == null)
                            {
                                var imageObject = new GameObject(
                                    "RobotModelPortrait",
                                    typeof(RectTransform),
                                    typeof(UnityEngine.UI.Image));
                                var imageTransform = (RectTransform)imageObject.transform;
                                imageTransform.SetParent(
                                    GameScreenManager.Instance.worldSpaceCanvas.transform,
                                    false);
                                row.RobotPortraitImage = imageObject.GetComponent<UnityEngine.UI.Image>();
                            }

                            var canvasTransform = GameScreenManager.Instance.worldSpaceCanvas.transform;
                            var imageRect = (RectTransform)row.RobotPortraitImage.transform;
                            imageRect.anchorMin = imageRect.anchorMax = new Vector2(0.5f, 0.5f);
                            imageRect.pivot = new Vector2(0.5f, 0.5f);
                            imageRect.localScale = Vector3.one;
                            var portraitWorldPosition = row.PortraitRenderer.transform.position;
                            imageRect.localPosition = canvasTransform.InverseTransformPoint(portraitWorldPosition);
                            var portraitWorldSize = robotSprite.bounds.size * 0.2f;
                            var portraitCanvasSize = canvasTransform.InverseTransformVector(
                                new Vector3(portraitWorldSize.x, portraitWorldSize.y, 0f));
                            imageRect.sizeDelta = new Vector2(
                                Mathf.Abs(portraitCanvasSize.x),
                                Mathf.Abs(portraitCanvasSize.y));
                            imageRect.SetAsLastSibling();
                            row.RobotPortraitImage.sprite = robotSprite;
                            row.RobotPortraitImage.preserveAspect = true;
                            row.RobotPortraitImage.raycastTarget = false;
                            row.RobotPortraitImage.color = new Color(1f, 1f, 1f, transparency);
                            row.RobotPortraitImage.enabled = true;
                            row.RobotPortraitImage.gameObject.SetActive(true);
                        }
                        else if (row.RobotPortraitImage != null)
                        {
                            row.RobotPortraitImage.enabled = false;
                        }
                    }
                    else
                    {
                        if (row.RobotPortraitImage != null)
                        {
                            row.RobotPortraitImage.enabled = false;
                        }

                        row.PortraitRenderer.sprite = entry.UseGenericPortrait
                            ? State.GetGenericModelPortrait()
                            : State.GetModelPortrait(entry.Model);
                        row.PortraitRenderer.enabled = row.PortraitRenderer.sprite != null;
                    }

                    if (entry.RobotModelTag.HasValue)
                    {
                        var spriteName = row.PortraitRenderer.sprite != null
                            ? row.PortraitRenderer.sprite.name
                            : row.RobotPortraitImage != null && row.RobotPortraitImage.sprite != null
                                ? row.RobotPortraitImage.sprite.name
                                : "null";
                        var imageRect = row.RobotPortraitImage != null
                            ? row.RobotPortraitImage.transform as RectTransform
                            : null;
                        LogDiagnosticOnce(
                            $"robot-sprite:{door.GetInstanceID()}:{entry.RobotModelTag.Value.Name}:{spriteName}",
                            $"[ConfigurableDoorIcons] Robot portrait: door={door.GetInstanceID()}, row={i}, model={entry.RobotModelTag.Value.Name}, sprite={spriteName}, rendererEnabled={row.PortraitRenderer.enabled}, uiImageEnabled={row.RobotPortraitImage != null && row.RobotPortraitImage.enabled}, uiImageActive={row.RobotPortraitImage != null && row.RobotPortraitImage.gameObject.activeInHierarchy}, rowPosition={row.GameObject.transform.position}, imagePosition={row.RobotPortraitImage?.transform.position}, imageLocalPosition={row.RobotPortraitImage?.transform.localPosition}, imageSize={imageRect?.sizeDelta.ToString() ?? "n/a"}, canvasScale={GameScreenManager.Instance.worldSpaceCanvas.transform.lossyScale}, alpha={row.RobotPortraitImage?.color.a}");
                    }
                }

                row.PermissionRenderer.sprite = State.DoorSprites.TryGetValue(entry.Permission, out var sprite)
                    ? sprite
                    : State.DoorSprites[ExtendedDoorState.Open];
                row.PortraitRenderer.color = new Color(1f, 1f, 1f, transparency);
                row.PermissionRenderer.color = new Color(1f, 1f, 1f, transparency);
            }
        }

        public static void UpdatePermissionIconScale(Door door)
        {
            if (!PermissionIcons.TryGetValue(door, out var rows) || rows.Count == 0 ||
                !State.DoorIcons.TryGetValue(door, out var doorIcon))
            {
                return;
            }

            var isHovered = IsPointerOverDoor(door);
            var scale = GetPermissionIconScale(door, rows.Count, isHovered);
            var alpha = isHovered ? 1f : Mathf.Clamp01(ConfigurableDoorIcons.Options.Instance.Transparency);
            var center = doorIcon.transform.position;
            var firstRowOffset = (rows.Count - 1) * 0.5f;
            for (var i = 0; i < rows.Count; i++)
            {
                var row = rows[i];
                row.GameObject.transform.localScale = new Vector3(0.005f * scale, 0.005f * scale, 1f);
                row.GameObject.transform.position = new Vector3(
                    center.x,
                    center.y + (firstRowOffset - i * 1f) * 0.5f * scale,
                    center.z);
                row.PortraitRenderer.sortingOrder = isHovered ? short.MaxValue : 0;
                row.PermissionRenderer.sortingOrder = isHovered ? short.MaxValue : 0;
                row.PortraitRenderer.color = new Color(1f, 1f, 1f, alpha);
                row.PermissionRenderer.color = new Color(1f, 1f, 1f, alpha);

                if (row.CrewPortrait != null)
                {
                    row.CrewPortrait.transform.localScale = Vector3.one * (0.01f * scale);
                    row.CrewPortrait.SetAlpha(alpha);
                    if (isHovered)
                    {
                        row.CrewPortrait.transform.SetAsLastSibling();
                    }

                    var portraitController = row.CrewPortrait.controller ??
                                             row.CrewPortrait.GetComponentInChildren<KBatchedAnimController>(true);
                    if (portraitController != null)
                    {
                        var portraitPosition = row.PortraitRenderer.bounds.center;
                        portraitController.transform.position = new Vector3(
                            portraitPosition.x - 0.03f * scale,
                            portraitPosition.y - 0.165f * scale,
                            portraitPosition.z);
                    }
                }

                if (row.RobotPortraitImage != null && row.RobotPortraitImage.sprite != null)
                {
                    var canvasTransform = GameScreenManager.Instance.worldSpaceCanvas.transform;
                    var imageRect = (RectTransform)row.RobotPortraitImage.transform;
                    imageRect.position = row.PortraitRenderer.transform.position;
                    var spriteSize = row.RobotPortraitImage.sprite.bounds.size * (0.2f * scale);
                    var canvasSize = canvasTransform.InverseTransformVector(
                        new Vector3(spriteSize.x, spriteSize.y, 0f));
                    imageRect.sizeDelta = new Vector2(Mathf.Abs(canvasSize.x), Mathf.Abs(canvasSize.y));
                    var imageColor = row.RobotPortraitImage.color;
                    imageColor.a = alpha;
                    row.RobotPortraitImage.color = imageColor;
                    if (isHovered)
                    {
                        imageRect.SetAsLastSibling();
                    }
                }
            }
        }

        private static float GetPermissionIconScale(Door door, int rowCount, bool isHovered)
        {
            if (rowCount < 5 || isHovered)
            {
                return 1f;
            }

            var building = door.GetComponent<Building>();
            if (building == null)
            {
                return 1f;
            }

            var height = building.Def.HeightInCells;
            var rotatable = door.GetComponent<Rotatable>();
            if (rotatable != null &&
                (rotatable.GetOrientation() == Orientation.R90 ||
                 rotatable.GetOrientation() == Orientation.R270))
            {
                height = building.Def.WidthInCells;
            }

            var availableHeight = Mathf.Max(1f, height);
            var unscaledHeight = rowCount * 0.5f;
            return Mathf.Min(1f, availableHeight / unscaledHeight);
        }

        private static bool IsPointerOverDoor(Door door)
        {
            var camera = Camera.main;
            if (camera == null || !State.DoorIcons.TryGetValue(door, out var doorIcon))
            {
                return false;
            }

            var center = doorIcon.transform.position;
            var building = door.GetComponent<Building>();
            if (building == null)
            {
                return false;
            }

            var width = building.Def.WidthInCells;
            var height = building.Def.HeightInCells;
            var rotatable = door.GetComponent<Rotatable>();
            if (rotatable != null &&
                (rotatable.GetOrientation() == Orientation.R90 ||
                 rotatable.GetOrientation() == Orientation.R270))
            {
                (width, height) = (height, width);
            }

            var screenCenter = camera.WorldToScreenPoint(center);
            var screenX = Mathf.Abs(camera.WorldToScreenPoint(center + Vector3.right * (width * 0.5f)).x - screenCenter.x);
            var screenY = Mathf.Abs(camera.WorldToScreenPoint(center + Vector3.up * (height * 0.5f)).y - screenCenter.y);
            var mouse = Input.mousePosition;
            return screenCenter.z > 0f &&
                   Mathf.Abs(mouse.x - screenCenter.x) <= screenX &&
                   Mathf.Abs(mouse.y - screenCenter.y) <= screenY;
        }

        private static List<PermissionIconRow> CreatePermissionIconRows(int count)
        {
            var rows = new List<PermissionIconRow>(count);
            for (var i = 0; i < count; i++)
            {
                var row = new GameObject("DoorPermissionIconRow");
                Util.KInstantiate(row, GameScreenManager.Instance.worldSpaceCanvas);
                row.transform.localScale = new Vector3(0.005f, 0.005f, 1f);

                var portrait = CreatePermissionIconPart(row.transform, "ModelPortrait", -43f, 0.48f);
                var permission = CreatePermissionIconPart(row.transform, "Permission", 43f, 0.36f);
                rows.Add(new PermissionIconRow(row, portrait, permission));
            }

            return rows;
        }

        private static SpriteRenderer CreatePermissionIconPart(
            Transform parent,
            string name,
            float xOffset,
            float scale)
        {
            var icon = new GameObject(name);
            icon.transform.SetParent(parent, false);
            icon.transform.localPosition = new Vector3(xOffset, 0f, 0f);
            icon.transform.localScale = new Vector3(scale, scale, 1f);
            var renderer = icon.AddComponent<SpriteRenderer>();
            renderer.material.renderQueue = 5000;
            return renderer;
        }

        private static void DestroyPermissionIcons(Door door)
        {
            if (!PermissionIcons.TryGetValue(door, out var rows))
            {
                return;
            }

            foreach (var row in rows)
            {
                if (row.CrewPortrait != null)
                {
                    GameObject.Destroy(row.CrewPortrait.gameObject);
                }

                if (row.RobotPortraitImage != null)
                {
                    GameObject.Destroy(row.RobotPortraitImage.gameObject);
                }

                GameObject.Destroy(row.GameObject);
            }

            PermissionIcons.Remove(door);
        }

        // TODO: remove also when removed via sandbox mode
        public static void RemoveIcon(Door door)
        {
            DestroyPermissionIcons(door);
            if (State.DoorIcons.TryGetValue(door, out var go))
            {
                GameObject.Destroy(go);

                State.DoorIcons.Remove(door);
            }
        }
    }
}