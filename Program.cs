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
        // private const int DEBUG = 0; //DEBUG
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
            Me.CustomData = "";

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

        //DEBUG begin
        // public void Debug(string msg, int level = 0)
        // {
        //     if (DEBUG <= 0 || (DEBUG & level) == 0) return;
        //     Me.CustomData += $"# {DateTime.Now:hh\\:mm\\:ss.f} {msg}\n";
        // }
        //DEBUG end

        private void SetupAirlock(EventLoop el, EventLoopTimer timer)
        {
            _blockGroups.Clear();
            GridTerminalSystem.GetBlockGroups(_blockGroups, bg => bg.Name.ToLower().Contains(_managedGroupMarker));
            var blockGroup = _blockGroups.Count == 0 ? null : _blockGroups[0];

            if (_cargoAirlock == null)
                _cargoAirlock = new CargoAirlock(el, _doorOpenTimeout, _actionTimeout);

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
            private bool _initialized = false;
            private AirlockState _status = AirlockState.Unknown;

            private readonly Dictionary<AirlockState, Dictionary<AirlockEvent, EventLoopTask>> _airlockStateMachine = new Dictionary<AirlockState, Dictionary<AirlockEvent, EventLoopTask>>();
            private readonly Dictionary<AirlockEvent, EventLoopProbe> _eventLookup = new Dictionary<AirlockEvent, EventLoopProbe>();
            private readonly Dictionary<AirlockState, EventLoopTimer> _timeoutLookup = new Dictionary<AirlockState, EventLoopTimer>();
            private EventLoopTimer _activeEventTimeout = null;

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

            public CargoAirlock(EventLoop el, long doorOpenTimeout, long errorTimeout)
            {
                _doorOpenTimeout = doorOpenTimeout;
                _errorTimeout = errorTimeout;
                foreach (var i in AirlockStates) _airlockStateMachine[i] = new Dictionary<AirlockEvent, EventLoopTask>();
                _airlockStateMachine[AirlockState.ExtOpenIntOpenPressurized][AirlockEvent.DoorOpenTimeout] = TaskCloseExternalDoor;
                _airlockStateMachine[AirlockState.ExtClosedIntOpenPressurized][AirlockEvent.DoorOpenTimeout] = TaskCloseInternalDoor;
                _airlockStateMachine[AirlockState.ExtClosedIntOpenPressurized][AirlockEvent.SensorInside] = TaskWayOut;
                _airlockStateMachine[AirlockState.ExtClosedIntOpenPressurized][AirlockEvent.SensorExternal] = TaskWayOut;
                _airlockStateMachine[AirlockState.ExtClosedIntOpenPressurized][AirlockEvent.SensorInternal] = null;
                _airlockStateMachine[AirlockState.ExtClosedIntClosedPressurized][AirlockEvent.SensorExternal] = TaskDepressurize;
                _airlockStateMachine[AirlockState.ExtClosedIntClosedPressurized][AirlockEvent.SensorInternal] = TaskWayOut;
                _airlockStateMachine[AirlockState.ExtClosedIntClosedPressurized][AirlockEvent.SensorInside] = TaskWayOut;
                _airlockStateMachine[AirlockState.ExtOpenIntClosedPressurized][AirlockEvent.DoorOpenTimeout] = TaskCloseExternalDoor;
                _airlockStateMachine[AirlockState.ExtClosedIntOpenDepressurized][AirlockEvent.DoorOpenTimeout] = TaskCloseInternalDoor;
                _airlockStateMachine[AirlockState.ExtClosedIntClosedDepressurized][AirlockEvent.SensorInternal] = TaskPressurize;
                _airlockStateMachine[AirlockState.ExtClosedIntClosedDepressurized][AirlockEvent.SensorInside] = TaskWayIn;
                _airlockStateMachine[AirlockState.ExtClosedIntClosedDepressurized][AirlockEvent.SensorExternal] = TaskWayIn;
                _airlockStateMachine[AirlockState.ExtOpenIntClosedDepressurized][AirlockEvent.DoorOpenTimeout] = TaskCloseExternalDoor;
                _airlockStateMachine[AirlockState.ExtOpenIntClosedDepressurized][AirlockEvent.SensorInside] = TaskWayIn;
                _airlockStateMachine[AirlockState.ExtOpenIntClosedDepressurized][AirlockEvent.SensorInternal] = TaskWayIn;
                _airlockStateMachine[AirlockState.ExtOpenIntClosedDepressurized][AirlockEvent.SensorExternal] = null;
                _airlockStateMachine[AirlockState.ExtOpenIntOpenDepressurized][AirlockEvent.DoorOpenTimeout] = TaskCloseInternalDoor;

                el.AddTask(InitializeProbes);
            }

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

                //TODO: set state if needed
                if (InternalDoorOpen()) SetInternalDoorState(AirlockState.InternalDoorOpen);
                else if (InternalDoorClosed()) SetInternalDoorState(AirlockState.InternalDoorClosed);
                if (ExternalDoorOpen()) SetExternalDoorState(AirlockState.ExternalDoorOpen);
                else if (ExternalDoorClosed()) SetExternalDoorState(AirlockState.ExternalDoorClosed);
                if (AirVentsDepressurized()) SetAirState(AirlockState.AirDepressurized);
                else if (AirVentsPressurized()) SetAirState(AirlockState.AirPressurized);
            }

            public string StatusDetails()
            {
                var title = _name + "\n" + new String('-', 50);
                var internalDoorStatus = (_status & AirlockState.InternalDoorClosed) != 0 ? "closed" :
                    (_status & AirlockState.InternalDoorClosing) != 0 ? "closing" :
                    (_status & AirlockState.InternalDoorOpen) != 0 ? "open" :
                    (_status & AirlockState.InternalDoorOpening) != 0 ? "opening" : "unknown";
                var externalDoorStatus = (_status & AirlockState.ExternalDoorClosed) != 0 ? "closed" :
                    (_status & AirlockState.ExternalDoorClosing) != 0 ? "closing" :
                    (_status & AirlockState.ExternalDoorOpen) != 0 ? "open" :
                    (_status & AirlockState.ExternalDoorOpening) != 0 ? "opening" : "unknown";
                var airStatus = (_status & AirlockState.AirPressurized) != 0 ? "pressurized" :
                    (_status & AirlockState.AirPressurizing) != 0 ? "pressurizing" :
                    (_status & AirlockState.AirDepressurized) != 0 ? "depressurized" :
                    (_status & AirlockState.AirDepressurizing) != 0 ? "depressurizing" : "unknown";
                var insideSensorStatus = _insideSensor == null ? "unknown" : _insideSensor.IsActive ? "active" : "inactive";
                var internalSensorStatus = _internalSensor == null ? "unknown" : _internalSensor.IsActive ? "active" : "inactive";
                var externalSensorStatus = _externalSensor == null ? "unknown" : _externalSensor.IsActive ? "active" : "inactive";
                var hasGyro = _gyrophare == null ? "no" : "yes";
                var activeProbes = _eventLookup.Count(e => e.Value.Active);
                return $@"{title}
Status: {_status}
Active probes: {activeProbes}/{_eventLookup.Count}
{_internalDoors.Count} internal doors {internalDoorStatus}
{_externalDoors.Count} external doors {externalDoorStatus}
{_airVents.Count} air vents {airStatus}
SAS inside sensor: {insideSensorStatus}
Internal sensor: {internalSensorStatus}
External sensor: {externalSensorStatus}
{_lights.Count} lights
Gyrophare: {hasGyro}";
            }

            private IEnumerable<EventLoopTask> InitializeProbes(EventLoop el)
            {
                if (_initialized) yield break;
                foreach (KeyValuePair<AirlockState, Dictionary<AirlockEvent, EventLoopTask>> eventMap in _airlockStateMachine)
                {
                    _timeoutLookup[eventMap.Key] = null;
                    foreach (KeyValuePair<AirlockEvent, EventLoopTask> eventEntry in eventMap.Value)
                    {
                        if (_eventLookup.ContainsKey(eventEntry.Key)) continue;

                        var eventType = eventEntry.Key;
                        Func<bool> condition = null;
                        switch (eventType)
                        {
                            case AirlockEvent.SensorInside:
                                condition = SensorInsideOn;
                                break;
                            case AirlockEvent.SensorInternal:
                                condition = SensorInternalOn;
                                break;
                            case AirlockEvent.SensorExternal:
                                condition = SensorExternalOn;
                                break;
                            default:
                                continue;
                        }
                        _eventLookup[eventType] = el.AddProbe(OnEvent(eventType), condition, 100);
                    }
                }
                el.SetInterval(UpdateProbes, 1000);
                _initialized = true;
            }

            private void UpdateProbes(EventLoop el, EventLoopTimer timer)
            {
                int countActive = 0;

                DisableProbes();
                if (_activeEventTimeout != null && _timeoutLookup.ContainsKey(_status) && _activeEventTimeout != _timeoutLookup[_status])
                {
                    // el.Debug($"UpdateProbes active timeout not for {_status}"); //DEBUG
                    el.CancelTimeout(_activeEventTimeout);
                }
                _activeEventTimeout = null;

                Dictionary<AirlockEvent, EventLoopTask> eventMap;
                if (!_airlockStateMachine.TryGetValue(_status, out eventMap))
                    return;
                foreach (KeyValuePair<AirlockEvent, EventLoopTask> eventTask in eventMap)
                {
                    if (eventTask.Key == AirlockEvent.DoorOpenTimeout)
                    {
                        if (!_timeoutLookup.ContainsKey(_status) || _timeoutLookup[_status] == null)
                        {
                            _timeoutLookup[_status] = el.SetTimeout(OnEventTimeout(eventTask.Value, _status), _doorOpenTimeout);
                        }
                        _activeEventTimeout = _timeoutLookup[_status];
                    }
                    else if (_eventLookup.ContainsKey(eventTask.Key))
                    {
                        _eventLookup[eventTask.Key].Enable();
                        countActive++;
                    }
                }

                // el.Debug($"UpdateProbes {countActive} active", 2); //DEBUG
            }

            private void DisableProbes()
            {
                foreach (var probe in _eventLookup.Values) probe.Disable();
            }

            private void CancelEventTimeout(EventLoop el)
            {
                EventLoopTimer eventTimeout;
                _timeoutLookup.TryGetValue(_status, out eventTimeout);
                if (_activeEventTimeout != eventTimeout)
                {
                    // el.Debug($"CancelEventTimeout active timeout not same as {_status}"); //DEBUG
                    el.CancelTimeout(eventTimeout);
                }
                el.CancelTimeout(_activeEventTimeout);
                _timeoutLookup[_status] = _activeEventTimeout = null;
            }

            private EventLoopProbeCallback OnEvent(AirlockEvent eventType)
            {
                return (el, probe) =>
                {
                    // el.Debug($"OnEvent({eventType}) : {_status}", 2); //DEBUG
                    DisableProbes();
                    CancelEventTimeout(el);
                    if (_airlockStateMachine.ContainsKey(_status))
                    {
                        var eventMap = _airlockStateMachine[_status];
                        if (eventMap.ContainsKey(eventType))
                        {
                            var eventTask = eventMap[eventType];
                            if (eventTask != null) el.AddTask(eventTask);
                        }
                    }
                };
            }

            private EventLoopTimerCallback OnEventTimeout(EventLoopTask task, AirlockState state)
            {
                return (el, timer) =>
                {
                    // el.Debug($"OnEventTimeout({state}) : {_status}", 2); //DEBUG
                    CancelEventTimeout(el);
                    DisableProbes();
                    el.AddTask(task);
                };
            }

            private IEnumerable<EventLoopTask> TaskCloseExternalDoor(EventLoop el)
            {
                // el.Debug($"TaskCloseExternalDoor() : {_status}", 1); //DEBUG
                SetExternalDoorState(AirlockState.ExternalDoorClosing);
                _externalDoors.ForEach(door => door.CloseDoor());
                yield return el.WaitFor(ExternalDoorClosed, 100, _errorTimeout);
                if (ExternalDoorClosed())
                    SetExternalDoorState(AirlockState.ExternalDoorClosed);
                else
                    _status |= AirlockState.Error;
            }

            private IEnumerable<EventLoopTask> TaskCloseInternalDoor(EventLoop el)
            {
                // el.Debug($"TaskCloseInternalDoor() : {_status}", 1); //DEBUG
                SetInternalDoorState(AirlockState.InternalDoorClosing);
                _internalDoors.ForEach(door => door.CloseDoor());
                yield return el.WaitFor(InternalDoorClosed, 100, _errorTimeout);
                if (InternalDoorClosed())
                    SetInternalDoorState(AirlockState.InternalDoorClosed);
                else
                    _status |= AirlockState.Error;
            }

            private IEnumerable<EventLoopTask> TaskOpenExternalDoor(EventLoop el)
            {
                // el.Debug($"TaskOpenExternalDoor() : {_status}", 1); //DEBUG
                SetExternalDoorState(AirlockState.ExternalDoorOpening);
                _externalDoors.ForEach(door => door.OpenDoor());
                yield return el.WaitFor(ExternalDoorOpen, 100, _errorTimeout);
                if (ExternalDoorOpen())
                    SetExternalDoorState(AirlockState.ExternalDoorOpen);
                else
                    _status |= AirlockState.Error;
            }
            private IEnumerable<EventLoopTask> TaskOpenInternalDoor(EventLoop el)
            {
                // el.Debug($"TaskOpenInternalDoor() : {_status}", 1); //DEBUG
                SetInternalDoorState(AirlockState.InternalDoorOpening);
                _internalDoors.ForEach(door => door.OpenDoor());
                yield return el.WaitFor(InternalDoorOpen, 100, _errorTimeout);
                if (InternalDoorOpen())
                    SetInternalDoorState(AirlockState.InternalDoorOpen);
                else
                    _status |= AirlockState.Error;
            }
            private IEnumerable<EventLoopTask> TaskWayOut(EventLoop el)
            {
                // el.Debug($"TaskWayOut() : {_status}", 1); //DEBUG
                _status |= AirlockState.ExitCycling;
                EventLoopTimer lightsTimer = null;
                if (!AirVentsPressurized()) goto TerminateTask;
                TurnOffLights();
                yield return TaskOpenInternalDoor;
                if (ErrorStatus) yield break;
                lightsTimer = el.SetInterval(RollLightsReverse, _rollingLightTime);
                while (true)
                {
                    yield return el.WaitFor(SensorInsideOn, 100, _errorTimeout);
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
                yield return el.WaitFor(SensorInsideOff, 100, _errorTimeout);

            TerminateTask:
                el.CancelTimeout(lightsTimer);
                TurnOffLights();
                _status &= ~AirlockState.ExitCycling;
                // el.Debug($"TaskWayOut finished", 1); //DEBUG
            }

            private IEnumerable<EventLoopTask> TaskWayIn(EventLoop el)
            {
                // el.Debug($"TaskWayIn() : {_status}", 1); //DEBUG
                _status |= AirlockState.EntryCycling;
                EventLoopTimer lightsTimer = null;
                if (!AirVentsDepressurized()) goto TerminateTask;
                TurnOffLights();
                yield return TaskOpenExternalDoor;
                if (ErrorStatus) yield break;
                lightsTimer = el.SetInterval(RollLights, _rollingLightTime);
                while (true)
                {
                    yield return el.WaitFor(SensorInsideOn, 100, _errorTimeout);
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
                yield return el.WaitFor(SensorInsideOff, 100, _errorTimeout);

            TerminateTask:
                el.CancelTimeout(lightsTimer);
                TurnOffLights();
                _status &= ~AirlockState.EntryCycling;
                // el.Debug($"TaskWayIn finished", 1); //DEBUG
            }

            private IEnumerable<EventLoopTask> TaskDepressurize(EventLoop el)
            {
                // el.Debug($"TaskDepressurize() : {_status}", 1); //DEBUG
                _airVents.ForEach(airVent => airVent.Depressurize = true);
                if (_gyrophare != null) _gyrophare.Enabled = true;
                SetAirState(AirlockState.AirDepressurizing);
                yield return el.WaitFor(AirVentsDepressurized, 100, _errorTimeout);
                SetAirState(AirlockState.AirDepressurized);
                if (_gyrophare != null) _gyrophare.Enabled = false;
            }

            private IEnumerable<EventLoopTask> TaskPressurize(EventLoop el)
            {
                // el.Debug($"TaskPressurize() : {_status}", 1); //DEBUG
                _airVents.ForEach(airVent => airVent.Depressurize = false);
                if (_gyrophare != null) _gyrophare.Enabled = true;
                SetAirState(AirlockState.AirPressurizing);
                yield return el.WaitFor(AirVentsPressurized, 100, _errorTimeout);
                if (AirVentsPressurized())
                    SetAirState(AirlockState.AirPressurized);
                else
                    _status |= AirlockState.Error;
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
            
            private bool ErrorStatus => (_status & AirlockState.Error) != 0;
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

            private void SetExternalDoorState(AirlockState x) => _status = (_status & ~(AirlockState.ExternalDoorOpen | AirlockState.ExternalDoorOpening | AirlockState.ExternalDoorClosed | AirlockState.ExternalDoorClosing)) | x;
            private void SetInternalDoorState(AirlockState x) => _status = (_status & ~(AirlockState.InternalDoorOpen | AirlockState.InternalDoorOpening | AirlockState.InternalDoorClosed | AirlockState.InternalDoorClosing)) | x;
            private void SetAirState(AirlockState x) => _status = (_status & ~(AirlockState.AirDepressurized | AirlockState.AirDepressurizing | AirlockState.AirPressurized | AirlockState.AirPressurizing)) | x;

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
            enum AirlockState
            {
                Unknown = 0x0000,
                ExternalDoorClosed = 0x01,
                ExternalDoorClosing = 0x02,
                ExternalDoorOpen = 0x04,
                ExternalDoorOpening = 0x08,
                InternalDoorClosed = 0x10,
                InternalDoorClosing = 0x20,
                InternalDoorOpen = 0x40,
                InternalDoorOpening = 0x80,
                AirPressurized = 0x0100,
                AirPressurizing = 0x0200,
                AirDepressurized = 0x0400,
                AirDepressurizing = 0x0800,
                EntryCycling = 0x1000,
                ExitCycling = 0x2000,
                Error = 0x8000,

                ExtOpenIntOpenDepressurized = ExternalDoorOpen | InternalDoorOpen | AirDepressurized,
                ExtClosedIntOpenDepressurized = ExternalDoorClosed | InternalDoorOpen | AirDepressurized,
                ExtOpenIntClosedDepressurized = ExternalDoorOpen | InternalDoorClosed | AirDepressurized,
                ExtClosedIntClosedDepressurized = ExternalDoorClosed | InternalDoorClosed | AirDepressurized,
                ExtOpenIntOpenPressurized = ExternalDoorOpen | InternalDoorOpen | AirPressurized,
                ExtClosedIntOpenPressurized = ExternalDoorClosed | InternalDoorOpen | AirPressurized,
                ExtOpenIntClosedPressurized = ExternalDoorOpen | InternalDoorClosed | AirPressurized,
                ExtClosedIntClosedPressurized = ExternalDoorClosed | InternalDoorClosed | AirPressurized,
                ExtOpenIntClosedDepressurizedOnWayIn = ExternalDoorOpen | InternalDoorClosed | AirDepressurized | EntryCycling,
                ExtClosedIntClosedDepressurizedOnWayIn = ExternalDoorClosed | InternalDoorClosed | AirDepressurized | EntryCycling,
                ExtClosedIntOpenPressurizedOnWayIn = ExternalDoorClosed | InternalDoorOpen | AirPressurized | EntryCycling,
                ExtClosedIntClosedPressurizedOnWayIn = ExternalDoorClosed | InternalDoorClosed | AirPressurized | EntryCycling,
                ExtOpenIntClosedDepressurizedOnWayOut = ExternalDoorOpen | InternalDoorClosed | AirDepressurized | ExitCycling,
                ExtClosedIntClosedDepressurizedOnWayOut = ExternalDoorClosed | InternalDoorClosed | AirDepressurized | ExitCycling,
                ExtClosedIntOpenPressurizedOnWayOut = ExternalDoorClosed | InternalDoorOpen | AirPressurized | ExitCycling,
                ExtClosedIntClosedPressurizedOnWayOut = ExternalDoorClosed | InternalDoorClosed | AirPressurized | ExitCycling,
            }
            static readonly AirlockState[] AirlockStates = {
                AirlockState.ExtOpenIntOpenDepressurized,
                AirlockState.ExtClosedIntOpenDepressurized,
                AirlockState.ExtOpenIntClosedDepressurized,
                AirlockState.ExtClosedIntClosedDepressurized,
                AirlockState.ExtOpenIntOpenPressurized,
                AirlockState.ExtClosedIntOpenPressurized,
                AirlockState.ExtOpenIntClosedPressurized,
                AirlockState.ExtClosedIntClosedPressurized,
                AirlockState.ExtOpenIntClosedDepressurizedOnWayIn,
                AirlockState.ExtClosedIntClosedDepressurizedOnWayIn,
                AirlockState.ExtClosedIntOpenPressurizedOnWayIn,
                AirlockState.ExtClosedIntClosedPressurizedOnWayIn,
                AirlockState.ExtOpenIntClosedDepressurizedOnWayOut,
                AirlockState.ExtClosedIntClosedDepressurizedOnWayOut,
                AirlockState.ExtClosedIntOpenPressurizedOnWayOut,
                AirlockState.ExtClosedIntClosedPressurizedOnWayOut,
            };
            enum AirlockEvent
            {
                DoorOpenTimeout = 0x0001,
                SensorInside = 0x0002,
                SensorInternal = 0x0004,
                SensorExternal = 0x0008,
            }

        }
    }
}
