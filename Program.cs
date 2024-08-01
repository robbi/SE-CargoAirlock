using Sandbox.Game.EntityComponents;
using Sandbox.ModAPI.Ingame;
using Sandbox.ModAPI.Interfaces;
using SpaceEngineers.Game.ModAPI.Ingame;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Configuration.Provider;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Timers;
using VRage;
using VRage.Collections;
using VRage.Game;
using VRage.Game.Components;
using VRage.Game.GUI.TextPanel;
using VRage.Game.ModAPI.Ingame;
using VRage.Game.ModAPI.Ingame.Utilities;
using VRage.Game.ObjectBuilders.Definitions;
using VRageMath;

namespace IngameScript
{
    partial class Program : MyGridProgram
    {
        //TODO read parameters using MyIni
        private const string Manage_group_named = "CargoAirlock";
        private const string Interior_door_name = "internal";
        private const string Exterior_door_name = "external";
        private const double Block_setup_refresh_time_in_sec = 15;
        private const double Min_time_doors_kept_open_in_sec = 5;
        private const double Max_action_time_in_sec = 30;

        /*
         * Please do not change anything past this point
         */
        private readonly string _managedGroupMarker;
        private readonly string _internalDoorMarker;
        private readonly string _externalDoorMarker;
        private readonly long _setupRefreshTime;
        private readonly long _doorOpenTimeout;
        private readonly long _actionTimeout;

        private readonly List<IMyBlockGroup> _blockGroups = new List<IMyBlockGroup>();
        private CargoAirlock _cargoAirlock;

        public Program()
        {
            _managedGroupMarker = string.IsNullOrWhiteSpace(Manage_group_named) ? "cargoairlock" : Manage_group_named.ToLower();
            _internalDoorMarker = string.IsNullOrWhiteSpace(Interior_door_name) ? "internal" : Interior_door_name.ToLower();
            _externalDoorMarker = string.IsNullOrWhiteSpace(Exterior_door_name) ? "external" : Exterior_door_name.ToLower();
            _setupRefreshTime = TimeSpan.FromSeconds(Block_setup_refresh_time_in_sec > 0.0 ? Block_setup_refresh_time_in_sec : 15.0).Ticks / TimeSpan.TicksPerMillisecond;
            _doorOpenTimeout = TimeSpan.FromSeconds(Min_time_doors_kept_open_in_sec >= 0.0 ? Min_time_doors_kept_open_in_sec : 5.0).Ticks / TimeSpan.TicksPerMillisecond;
            _actionTimeout = TimeSpan.FromSeconds(Max_action_time_in_sec >= 0.0 ? Max_action_time_in_sec : 30.0).Ticks / TimeSpan.TicksPerMillisecond;

            var el = InitializeEventLoop(this, 5);
            el.SetInterval(SetupAirlock, _setupRefreshTime);
            el.SetInterval(EchoScriptStatus, 1000);
        }

        public void Main(string argument, UpdateType updateSource)
        {
            if ((updateSource & (UpdateType.Once | UpdateType.Update1 | UpdateType.Update10 | UpdateType.Update100)) != 0)
            {
                RunEventLoop();
            }
        }

        private void SetupAirlock(EventLoop el, EventLoopTimer timer)
        {
            _blockGroups.Clear();
            GridTerminalSystem.GetBlockGroups(_blockGroups, bg => bg.Name.ToLower().Contains(_managedGroupMarker));
            var blockGroup = _blockGroups.Count == 0 ? null : _blockGroups[0];

            if (_cargoAirlock == null)
                _cargoAirlock = new CargoAirlock(_doorOpenTimeout, _actionTimeout);

            _cargoAirlock.Setup(blockGroup, _internalDoorMarker, _externalDoorMarker);
        }

        private void EchoScriptStatus(EventLoop el, EventLoopTimer timer)
        {
            if (_cargoAirlock == null)
                Echo("No airlock managed");
            else
                Echo(_cargoAirlock.StatusDetails());
        }

        public class CargoAirlock
        {
            private string _name;
            private readonly long _doorOpenTimeout;
            private readonly long _errorTimeout;
            private readonly int _rollingLightTime = 100;
            private readonly int _rollingLightOnCount = 3;
            private readonly StateMachine<AirlockState, AirlockEvent> _stateMachine;

            private readonly List<IMyTerminalBlock> _blocks = new List<IMyTerminalBlock>();
            private readonly List<IMyDoor> _internalDoors = new List<IMyDoor>();
            private readonly List<IMyDoor> _externalDoors = new List<IMyDoor>();
            private readonly List<IMyAirVent> _airVents = new List<IMyAirVent>();
            private IMySensorBlock _internalSensor;
            private IMySensorBlock _externalSensor;
            private IMySensorBlock _insideSensor;
            private readonly List<WayLight> _lights = new List<WayLight>();
            private IMyLightingBlock _gyrophare = null;
            private Vector3I _externalDoorPosition;

            public CargoAirlock(long doorOpenTimeout, long errorTimeout)
            {
                _doorOpenTimeout = doorOpenTimeout;
                _errorTimeout = errorTimeout;

                var sm = new StateMachine<AirlockState, AirlockEvent>(updateInterval: 100);
                _stateMachine = sm;
                sm.SetEventTimeout(AirlockState.ExtOpenIntOpenPressurized, doorOpenTimeout, TaskCloseExternalDoor);
                sm.SetEventTimeout(AirlockState.ExtClosedIntOpenPressurized, doorOpenTimeout, TaskCloseInternalDoor);
                sm.SetEventHandler(AirlockState.ExtClosedIntOpenPressurized, AirlockEvent.SensorInside, SensorInsideOn, TaskWayOut);
                sm.SetEventHandler(AirlockState.ExtClosedIntOpenPressurized, AirlockEvent.SensorExternal, SensorExternalOn, TaskWayOut);
                sm.SetEventHandler(AirlockState.ExtClosedIntOpenPressurized, AirlockEvent.SensorInternal, SensorInternalOn, null);
                sm.SetEventHandler(AirlockState.ExtClosedIntClosedPressurized, AirlockEvent.SensorExternal, SensorExternalOn, TaskDepressurize);
                sm.SetEventHandler(AirlockState.ExtClosedIntClosedPressurized, AirlockEvent.SensorInternal, SensorInternalOn, TaskWayOut);
                sm.SetEventHandler(AirlockState.ExtClosedIntClosedPressurized, AirlockEvent.SensorInside, SensorInsideOn, TaskWayOut);
                sm.SetEventTimeout(AirlockState.ExtOpenIntClosedPressurized, doorOpenTimeout, TaskCloseExternalDoor);
                sm.SetEventTimeout(AirlockState.ExtClosedIntOpenDepressurized, doorOpenTimeout, TaskCloseInternalDoor);
                sm.SetEventHandler(AirlockState.ExtClosedIntClosedDepressurized, AirlockEvent.SensorInternal, SensorInternalOn, TaskPressurize);
                sm.SetEventHandler(AirlockState.ExtClosedIntClosedDepressurized, AirlockEvent.SensorInside, SensorInsideOn, TaskWayIn);
                sm.SetEventHandler(AirlockState.ExtClosedIntClosedDepressurized, AirlockEvent.SensorExternal, SensorExternalOn, TaskWayIn);
                sm.SetEventTimeout(AirlockState.ExtOpenIntClosedDepressurized, doorOpenTimeout, TaskCloseExternalDoor);
                sm.SetEventHandler(AirlockState.ExtOpenIntClosedDepressurized, AirlockEvent.SensorInside, SensorInsideOn, TaskWayIn);
                sm.SetEventHandler(AirlockState.ExtOpenIntClosedDepressurized, AirlockEvent.SensorInternal, SensorInternalOn, TaskWayIn);
                sm.SetEventHandler(AirlockState.ExtOpenIntClosedDepressurized, AirlockEvent.SensorExternal, SensorExternalOn, null);
                sm.SetEventTimeout(AirlockState.ExtOpenIntOpenDepressurized, doorOpenTimeout, TaskCloseInternalDoor);
            }

            private void SetStatus(AirlockState status) => _stateMachine.SetState(status);
            private void SetErrorStatus() => _stateMachine.SetState(_stateMachine.CurrentState | AirlockState.Error);

            public void Setup(IMyBlockGroup blockGroup, string interiorMarker, string exteriorMarker)
            {
                _name = blockGroup == null ? "<undefined>" : blockGroup.Name;

                _internalDoors.Clear();
                _externalDoors.Clear();
                _airVents.Clear();
                _internalSensor = null;
                _externalSensor = null;
                _insideSensor = null;
                var oldWayLights = _lights.ToArray();
                _lights.Clear();
                _gyrophare = null;

                if (blockGroup == null) return;

                _blocks.Clear();
                blockGroup.GetBlocks(_blocks);
                foreach (var block in _blocks)
                {
                    if (block is IMyDoor)
                    {
                        IMyDoor door = (IMyDoor)block;
                        var doorName = door.CustomName.ToLower();
                        if (doorName.Contains(interiorMarker)) _internalDoors.Add(door);
                        if (doorName.Contains(exteriorMarker)) _externalDoors.Add(door);
                        continue;
                    }
                    if (block is IMySensorBlock)
                    {
                        IMySensorBlock sensor = (IMySensorBlock)block;
                        var sensorName = sensor.CustomName.ToLower();
                        if (sensorName.Contains(interiorMarker))
                        {
                            _internalSensor = sensor;
                        }
                        else if (sensorName.Contains(exteriorMarker))
                        {
                            _externalSensor = sensor;
                        }
                        else
                        {
                            _insideSensor = sensor;
                        }
                        continue;
                    }
                    if (block is IMyAirVent)
                    {
                        _airVents.Add((IMyAirVent)block);
                        continue;
                    }
                    if (block is IMyLightingBlock)
                    {
                        IMyLightingBlock light = (IMyLightingBlock)block;
                        var lightName = light.CustomName.ToLower();
                        if (lightName.Contains("gyro"))
                        {
                            _gyrophare = light;
                            continue;
                        }
                        WayLight wayLight = Array.Find(oldWayLights, x => x.Block == light);
                        if (wayLight == null)
                        {
                            wayLight = new WayLight(light);
                        }
                        _lights.Add(wayLight);
                        continue;
                    }
                }
                _blocks.Clear();

                if (_externalDoors.Count == 0) return;

                // Calculate external position
                var externalPosition = new Vector3I(0, 0, 0);
                foreach (var door in _externalDoors)
                {
                    externalPosition += door.Position;
                }
                externalPosition /= _externalDoors.Count;
                bool positionChanged = externalPosition != _externalDoorPosition;
                _externalDoorPosition = externalPosition;

                // Sort lights by distance to external door
                foreach (var light in _lights)
                {
                    if (positionChanged || light.Distance == 0)
                    {
                        light.Distance = _externalDoorPosition.RectangularDistance(light.Block.Position);
                    }
                }
                _lights.Sort((a, b) => a.Distance.CompareTo(b.Distance));

                AirlockState currentState = _stateMachine.CurrentState;
                AirlockState blocksState = AirlockState.Unknown;
                if (InternalDoorOpen()) blocksState = AirlockState.InternalDoorOpen;
                else if (InternalDoorClosed()) blocksState = AirlockState.InternalDoorClosed;
                if (blocksState != AirlockState.Unknown && (currentState & AirlockState.InternalDoor) != blocksState)
                    SetInternalDoorState(blocksState);

                if (ExternalDoorOpen()) blocksState = AirlockState.ExternalDoorOpen;
                else if (ExternalDoorClosed()) blocksState = AirlockState.ExternalDoorClosed;
                else blocksState = AirlockState.Unknown;
                if (blocksState != AirlockState.Unknown && (currentState & AirlockState.ExternalDoor) != blocksState)
                    SetExternalDoorState(blocksState);

                if (AirVentsDepressurized()) blocksState = AirlockState.AirDepressurized;
                else if (AirVentsPressurized()) blocksState = AirlockState.AirPressurized;
                else blocksState = AirlockState.Unknown;
                if (blocksState != AirlockState.Unknown && (currentState & AirlockState.Air) != blocksState)
                    SetAirState(blocksState);
            }

            public string StatusDetails()
            {
                var title = _name + "\n" + new String('-', 50);
                var internalDoorStatus = GetDoorsStatus(_internalDoors)?.ToString() ?? "unknown";
                var externalDoorStatus = GetDoorsStatus(_externalDoors)?.ToString() ?? "unknown";
                var airStatus = GetAirVentsStatus()?.ToString() ?? "unknown";
                var insideSensorStatus = _insideSensor == null ? "unknown" : _insideSensor.IsActive ? "active" : "inactive";
                var internalSensorStatus = _internalSensor == null ? "unknown" : _internalSensor.IsActive ? "active" : "inactive";
                var externalSensorStatus = _externalSensor == null ? "unknown" : _externalSensor.IsActive ? "active" : "inactive";
                var hasGyro = _gyrophare == null ? "no" : "yes";
                return $@"{title}
Status: {_stateMachine.CurrentState}
{_internalDoors.Count} internal doors {internalDoorStatus}
{_externalDoors.Count} external doors {externalDoorStatus}
{_airVents.Count} air vents {airStatus}
SAS inside sensor: {insideSensorStatus}
Internal sensor: {internalSensorStatus}
External sensor: {externalSensorStatus}
{_lights.Count} lights
Gyrophare: {hasGyro}";
            }

            private static DoorStatus? GetDoorsStatus(List<IMyDoor> doors)
            {
                DoorStatus? status = null;
                foreach (var door in doors)
                {
                    if (status == null) status = door.Status;
                    else if (status != door.Status) return null;
                }
                return status;
            }

            private VentStatus? GetAirVentsStatus()
            {
                VentStatus? status = null;
                foreach (var airVent in _airVents)
                {
                    if (status == null) status = airVent.Status;
                    else if (status != airVent.Status) return null;
                }
                return status;
            }

            private IEnumerable<EventLoopTask> TaskWayIn(EventLoop el)
            {
                SetStatus(_stateMachine.CurrentState | AirlockState.EntryCycling);
                EventLoopTimer lightsTimer = null;
                if (!AirVentsDepressurized()) goto TerminateTask;
                TurnOffLights();
                yield return TaskOpenExternalDoor;
                if (ErrorStatus) yield break;
                lightsTimer = el.SetInterval(RollLights, _rollingLightTime);
                while (true)
                {
                    yield return _stateMachine.WaitFor(SensorInsideOn, _errorTimeout);
                    if (SensorExternalOn()) continue;
                    if (!SensorInsideOn()) goto TerminateTask;
                    break;
                }
                yield return TaskCloseExternalDoor;
                if (ErrorStatus) yield break;
                yield return TaskPressurize;
                if (ErrorStatus) yield break;
                yield return TaskOpenInternalDoor;
                if (ErrorStatus) yield break;
                yield return _stateMachine.WaitFor(SensorInsideOff, _errorTimeout);

            TerminateTask:
                el.CancelTimer(lightsTimer);
                TurnOffLights();
                SetStatus(_stateMachine.CurrentState & ~AirlockState.EntryCycling);
            }

            private IEnumerable<EventLoopTask> TaskWayOut(EventLoop el)
            {
                SetStatus(_stateMachine.CurrentState | AirlockState.ExitCycling);
                EventLoopTimer lightsTimer = null;
                if (!AirVentsPressurized()) goto TerminateTask;
                TurnOffLights();
                yield return TaskOpenInternalDoor;
                if (ErrorStatus) yield break;
                lightsTimer = el.SetInterval(RollLightsReverse, _rollingLightTime);
                while (true)
                {
                    yield return _stateMachine.WaitFor(SensorInsideOn, _errorTimeout);
                    if (SensorInternalOn()) continue;
                    if (!SensorInsideOn()) goto TerminateTask;
                    break;
                }
                yield return TaskCloseInternalDoor;
                if (ErrorStatus) yield break;
                yield return TaskDepressurize;
                if (ErrorStatus) yield break;
                yield return TaskOpenExternalDoor;
                if (ErrorStatus) yield break;
                yield return _stateMachine.WaitFor(SensorInsideOff, _errorTimeout);

            TerminateTask:
                el.CancelTimer(lightsTimer);
                TurnOffLights();
                SetStatus(_stateMachine.CurrentState & ~AirlockState.ExitCycling);
            }

            private IEnumerable<EventLoopTask> TaskCloseExternalDoor(EventLoop el)
            {
                SetExternalDoorState(AirlockState.ExternalDoorClosing);
                _externalDoors.ForEach(door => door.CloseDoor());
                yield return _stateMachine.WaitFor(ExternalDoorClosed, _errorTimeout);
                if (ExternalDoorClosed())
                    SetExternalDoorState(AirlockState.ExternalDoorClosed);
                else
                    SetErrorStatus();
            }

            private IEnumerable<EventLoopTask> TaskCloseInternalDoor(EventLoop el)
            {
                SetInternalDoorState(AirlockState.InternalDoorClosing);
                _internalDoors.ForEach(door => door.CloseDoor());
                yield return _stateMachine.WaitFor(InternalDoorClosed, _errorTimeout);
                if (InternalDoorClosed())
                    SetInternalDoorState(AirlockState.InternalDoorClosed);
                else
                    SetErrorStatus();
            }

            private IEnumerable<EventLoopTask> TaskOpenExternalDoor(EventLoop el)
            {
                SetExternalDoorState(AirlockState.ExternalDoorOpening);
                _externalDoors.ForEach(door => door.OpenDoor());
                yield return _stateMachine.WaitFor(ExternalDoorOpen, _errorTimeout);
                if (ExternalDoorOpen())
                    SetExternalDoorState(AirlockState.ExternalDoorOpen);
                else
                    SetErrorStatus();
            }

            private IEnumerable<EventLoopTask> TaskOpenInternalDoor(EventLoop el)
            {
                SetInternalDoorState(AirlockState.InternalDoorOpening);
                _internalDoors.ForEach(door => door.OpenDoor());
                yield return _stateMachine.WaitFor(InternalDoorOpen, _errorTimeout);
                if (InternalDoorOpen())
                    SetInternalDoorState(AirlockState.InternalDoorOpen);
                else
                    SetErrorStatus();
            }

            private IEnumerable<EventLoopTask> TaskDepressurize(EventLoop el)
            {
                _airVents.ForEach(airVent => airVent.Depressurize = true);
                if (_gyrophare != null) _gyrophare.Enabled = true;
                SetAirState(AirlockState.AirDepressurizing);
                yield return _stateMachine.WaitFor(AirVentsDepressurized, _errorTimeout);
                SetAirState(AirlockState.AirDepressurized);
                if (_gyrophare != null) _gyrophare.Enabled = false;
            }

            private IEnumerable<EventLoopTask> TaskPressurize(EventLoop el)
            {
                _airVents.ForEach(airVent => airVent.Depressurize = false);
                if (_gyrophare != null) _gyrophare.Enabled = true;
                SetAirState(AirlockState.AirPressurizing);
                yield return _stateMachine.WaitFor(AirVentsPressurized, _errorTimeout);
                if (AirVentsPressurized())
                    SetAirState(AirlockState.AirPressurized);
                else
                    SetErrorStatus();
                if (_gyrophare != null) _gyrophare.Enabled = false;
            }

            private int _rollingLightIndex;

            private void RollLights(EventLoop el, EventLoopTimer timer)
            {
                var lightsCount = _lights.Count;
                if (lightsCount == 0) return;
                var lightIndex = (0 <= _rollingLightIndex && _rollingLightIndex < lightsCount) ? _rollingLightIndex : lightsCount - 1;
                _lights[lightIndex].Block.Enabled = false;
                lightIndex = (lightIndex + 1) % lightsCount;
                _rollingLightIndex = lightIndex;
                for (int i = 0; i < _rollingLightOnCount; i++)
                {
                    _lights[lightIndex].Block.Enabled = true;
                    lightIndex = (lightIndex + 1) % lightsCount;
                }
            }

            private void RollLightsReverse(EventLoop el, EventLoopTimer timer)
            {
                var lightsCount = _lights.Count;
                if (lightsCount == 0) return;
                var lightIndex = (0 <= _rollingLightIndex && _rollingLightIndex < lightsCount) ? _rollingLightIndex : 0;
                _lights[lightIndex].Block.Enabled = false;
                lightIndex = (lightIndex + lightsCount - 1) % lightsCount;
                _rollingLightIndex = lightIndex;
                for (int i = 0; i < _rollingLightOnCount; i++)
                {
                    _lights[lightIndex].Block.Enabled = true;
                    lightIndex = (lightIndex + lightsCount - 1) % lightsCount;
                }
            }

            private void TurnOffLights()
            {
                foreach (var light in _lights)
                {
                    light.Block.Enabled = false;
                }
                _rollingLightIndex = -1;
            }

            private bool ErrorStatus => (_stateMachine.CurrentState & AirlockState.Error) != 0;
            private bool ExternalDoorClosed() => _externalDoors.TrueForAll(door => door.Status == DoorStatus.Closed);
            private bool ExternalDoorOpen() => _externalDoors.TrueForAll(door => door.Status == DoorStatus.Open);
            private bool InternalDoorClosed() => _internalDoors.TrueForAll(door => door.Status == DoorStatus.Closed);
            private bool InternalDoorOpen() => _internalDoors.TrueForAll(door => door.Status == DoorStatus.Open);
            private bool SensorInsideOff() => !_insideSensor.IsActive;
            private bool SensorInsideOn() => _insideSensor.IsActive;
            private bool SensorInternalOn() => _internalSensor.IsActive;
            private bool SensorExternalOn() => _externalSensor.IsActive;
            private bool AirVentsDepressurized() => _airVents.TrueForAll(vent => vent.Status == VentStatus.Depressurized || vent.GetOxygenLevel() < 0.01);
            private bool AirVentsPressurized() => _airVents.TrueForAll(vent => vent.Status == VentStatus.Pressurized || vent.GetOxygenLevel() > 0.99);

            private void SetExternalDoorState(AirlockState x) => SetStatus((_stateMachine.CurrentState & ~AirlockState.ExternalDoor) | x);
            private void SetInternalDoorState(AirlockState x) => SetStatus((_stateMachine.CurrentState & ~AirlockState.InternalDoor) | x);
            private void SetAirState(AirlockState x) => SetStatus((_stateMachine.CurrentState & ~AirlockState.Air) | x);

            private class WayLight
            {
                public IMyLightingBlock Block;
                public int Distance;

                public WayLight(IMyLightingBlock light)
                {
                    Block = light;
                }
            }

            [Flags]
            enum AirlockState : uint
            {
                Unknown = 0x0000,
                ExternalDoorClosed = 0x0001,
                ExternalDoorClosing = 0x0002,
                ExternalDoorOpen = 0x0004,
                ExternalDoorOpening = 0x0008,
                InternalDoorClosed = 0x0010,
                InternalDoorClosing = 0x0020,
                InternalDoorOpen = 0x0040,
                InternalDoorOpening = 0x0080,
                AirPressurized = 0x0100,
                AirPressurizing = 0x0200,
                AirDepressurized = 0x0400,
                AirDepressurizing = 0x0800,
                EntryCycling = 0x1000,
                ExitCycling = 0x2000,
                Error = 0x8000,

                ExternalDoor = ExternalDoorClosed | ExternalDoorClosing | ExternalDoorOpen | ExternalDoorOpening,
                InternalDoor = InternalDoorClosed | InternalDoorClosing | InternalDoorOpen | InternalDoorOpening,
                Air = AirPressurized | AirPressurizing | AirDepressurized | AirDepressurizing,

                ExtOpenIntOpenDepressurized = ExternalDoorOpen | InternalDoorOpen | AirDepressurized,
                ExtClosedIntOpenDepressurized = ExternalDoorClosed | InternalDoorOpen | AirDepressurized,
                ExtOpenIntClosedDepressurized = ExternalDoorOpen | InternalDoorClosed | AirDepressurized,
                ExtClosedIntClosedDepressurized = ExternalDoorClosed | InternalDoorClosed | AirDepressurized,
                ExtOpenIntOpenPressurized = ExternalDoorOpen | InternalDoorOpen | AirPressurized,
                ExtClosedIntOpenPressurized = ExternalDoorClosed | InternalDoorOpen | AirPressurized,
                ExtOpenIntClosedPressurized = ExternalDoorOpen | InternalDoorClosed | AirPressurized,
                ExtClosedIntClosedPressurized = ExternalDoorClosed | InternalDoorClosed | AirPressurized,
            }

            enum AirlockEvent : uint
            {
                SensorInside = 1,
                SensorInternal = 2,
                SensorExternal = 3,
            }

        }
    }
}
