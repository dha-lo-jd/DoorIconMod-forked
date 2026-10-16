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
            else
            {
                switch (GetDoorStatus(door))
                {
                    case Door.ControlState.Auto:
                        if (HasCustomDupePermissions(accessControl))
                        {
                            return ExtendedDoorState.AccessCustom;
                        }

                        if (HasCustomGlobalPermissions(accessControl, out var permission) ||
                            (permission != ExtendedDoorState.Auto && permission != ExtendedDoorState.Invalid))
                        {
                            return permission;
                        }

                        return ExtendedDoorState.Auto;

                    case Door.ControlState.Opened:
                        if (HasCustomDupePermissions(accessControl))
                        {
                            return ExtendedDoorState.AccessCustom;
                        }

                        if (HasCustomGlobalPermissions(accessControl, out var permission2) ||
                            (permission2 != ExtendedDoorState.Auto && permission2 != ExtendedDoorState.Invalid))
                        {
                            return permission2;
                        }

                        return ExtendedDoorState.Open;

                    case Door.ControlState.Locked:
                        return ExtendedDoorState.Locked;
                }
            }

            return ExtendedDoorState.Invalid;
        }

        private static bool HasCustomGlobalPermissions(AccessControl access, out ExtendedDoorState doorState)
        {
            doorState = ExtendedDoorState.Invalid;
            var setDoorState = false;
            foreach (var tag in GameTags.Minions.Models.AllModels.Concat([GameTags.Robot]))
            {
                var thisDoorState = access.GetDefaultPermission(tag) switch
                {
                    AccessControl.Permission.Both => ExtendedDoorState.Auto,
                    AccessControl.Permission.GoLeft => ExtendedDoorState.AccessLeft,
                    AccessControl.Permission.GoRight => ExtendedDoorState.AccessRight,
                    AccessControl.Permission.Neither => ExtendedDoorState.AccessRestricted,
                    _ => ExtendedDoorState.Invalid,
                };

                if (!setDoorState)
                {
                    // First permission we encounter in the list
                    doorState = thisDoorState;
                    setDoorState = true;
                }
                else if (doorState != thisDoorState)
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
            return GetSavedPermissions(access).Any(p => p.Value != access.GetDefaultPermission(GetTagFromInstanceID(p.Key)));
        }

        private static Tag GetTagFromInstanceID(int id)
        {
            var prefab = KPrefabIDTracker.Get()?.GetInstance(id);
            if (prefab != null && prefab.GetComponent<MinionAssignablesProxy>() is var minion)
            {
                Debug.Log($"[ConfigurableDoorIcons] Prefab with instance ID {id} is a {minion.GetMinionModel()}");
                return minion.GetMinionModel();
            }

            Debug.Log($"[ConfigurableDoorIcons] Prefab with instance ID {id} has no matching tags. Assuming it's a robot?");
            return GameTags.Robot;
        }

        private static void SetIcon(Door door, ExtendedDoorState targetState)
        {
            if (State.DoorIcons.TryGetValue(door, out var go))
            {
                var renderer = go.GetComponent<SpriteRenderer>();

                if (renderer != null)
                {
                    if ((targetState == ExtendedDoorState.Open && !ConfigurableDoorIcons.Options.Instance.ShowOnOpenDoors) ||
                        (targetState == ExtendedDoorState.Locked && !ConfigurableDoorIcons.Options.Instance.ShowOnLockedDoors))
                    {
                        renderer.enabled = false;
                        return;
                    }

                    if (State.DoorSprites.TryGetValue(targetState, out var newSprite))
                    {
                        renderer.sprite = newSprite;
                        renderer.enabled = newSprite != null;
                    }
                    else
                    {
                        renderer.enabled = false;
                    }
                }
            }
        }

        // TODO: remove also when removed via sandbox mode
        public static void RemoveIcon(Door door)
        {
            if (State.DoorIcons.TryGetValue(door, out var go))
            {
                GameObject.Destroy(go);

                State.DoorIcons.Remove(door);
            }
        }
    }
}
