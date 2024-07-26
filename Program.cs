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
        private const string Manage_group_named = "CargoAirlock";
        private const string Interior_door_name = "internal";
        private const string Exterior_door_name = "external";
        private const double Block_setup_refresh_time_in_sec = 15;
        private const double Min_time_doors_kept_open_in_sec = 5;
        private const double Max_action_time_in_sec = 30;
        private const int DEBUG = 0;

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

        public void Debug(string msg, int level=0)
        {
            if (DEBUG <= 0 || DEBUG < level) return;
            Me.CustomData += $"# {DateTime.Now} {msg}\n";
        }

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
            private long _doorOpenTimeout;
            private long _errorTimeout;
            private bool _initialized = false;
            private AirlockState _status = AirlockState.Unknown;

            private readonly Dictionary<AirlockState, Dictionary<AirlockEvent, EventLoopTask>> _airlockStateMachine = new Dictionary<AirlockState, Dictionary<AirlockEvent, EventLoopTask>>();
            private readonly Dictionary<AirlockEvent, EventLoopProbe> _eventLookup = new Dictionary<AirlockEvent, EventLoopProbe>();
            private readonly Dictionary<AirlockState, EventLoopTimer> _timeoutLookup = new Dictionary<AirlockState, EventLoopTimer>();

            private List<IMyDoor> _internalDoors = new List<IMyDoor>();
            private List<IMyDoor> _externalDoors = new List<IMyDoor>();
            private List<IMyAirVent> _airVents = new List<IMyAirVent>();
            private IMySensorBlock _internalSensor;
            private IMySensorBlock _externalSensor;
            private IMySensorBlock _insideSensor;
            private List<IMyLightingBlock> _lights = new List<IMyLightingBlock>();
            private IMyLightingBlock _gyrophare = null;

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
                _airlockStateMachine[AirlockState.ExtClosedIntClosedPressurized][AirlockEvent.SensorInternal] = TaskOpenInternalDoor;
                _airlockStateMachine[AirlockState.ExtClosedIntClosedPressurized][AirlockEvent.SensorInside] = TaskOpenInternalDoor;
                _airlockStateMachine[AirlockState.ExtOpenIntClosedPressurized][AirlockEvent.DoorOpenTimeout] = TaskCloseExternalDoor;
                _airlockStateMachine[AirlockState.ExtClosedIntOpenDepressurized][AirlockEvent.DoorOpenTimeout] = TaskCloseInternalDoor;
                _airlockStateMachine[AirlockState.ExtClosedIntClosedDepressurized][AirlockEvent.SensorInternal] = TaskPressurize;
                _airlockStateMachine[AirlockState.ExtClosedIntClosedDepressurized][AirlockEvent.SensorInside] = TaskOpenExternalDoor;
                _airlockStateMachine[AirlockState.ExtClosedIntClosedDepressurized][AirlockEvent.SensorExternal] = TaskOpenExternalDoor;
                _airlockStateMachine[AirlockState.ExtOpenIntClosedDepressurized][AirlockEvent.DoorOpenTimeout] = TaskCloseExternalDoor;
                _airlockStateMachine[AirlockState.ExtOpenIntClosedDepressurized][AirlockEvent.SensorInside] = TaskWayIn;
                _airlockStateMachine[AirlockState.ExtOpenIntClosedDepressurized][AirlockEvent.SensorInternal] = TaskWayIn;
                _airlockStateMachine[AirlockState.ExtOpenIntOpenDepressurized][AirlockEvent.DoorOpenTimeout] = TaskCloseInternalDoor;

                el.AddTask(InitializeProbes);
            }

            public void Setup(IMyBlockGroup blockGroup, string lcInteriorSuffix, string lcExteriorSuffix)
            {
                _name = blockGroup == null ? "<undefined>" : blockGroup.Name;

                _internalDoors.Clear();
                _externalDoors.Clear();
                _airVents.Clear();
                _internalSensor = null;
                _externalSensor = null;
                _insideSensor = null;
                _lights.Clear();
                _gyrophare = null;

                if (blockGroup == null) return;
                //TODO optimize block parsing
                //TODO identify change and keep refs if not

                var doors = new List<IMyDoor>();
                blockGroup.GetBlocksOfType(doors, block => block.IsFunctional);
                if (doors.Count == 0) return;

                foreach (var door in doors)
                {
                    var doorName = door.CustomName.ToLower();
                    if (doorName.Contains(lcInteriorSuffix)) _internalDoors.Add(door);
                    if (doorName.Contains(lcExteriorSuffix)) _externalDoors.Add(door);
                }
                doors.Clear();

                blockGroup.GetBlocksOfType(_airVents, block => block.IsFunctional);

                var sensors = new List<IMySensorBlock>();
                blockGroup.GetBlocksOfType(sensors);
                foreach (var sensor in sensors)
                {
                    var sensorName = sensor.CustomName.ToLower();
                    if (sensorName.Contains(lcInteriorSuffix))
                    {
                        _internalSensor = sensor;
                    }
                    else if (sensorName.Contains(lcExteriorSuffix))
                    {
                        _externalSensor = sensor;
                    }
                    else
                    {
                        _insideSensor = sensor;
                    }
                }
                sensors.Clear();

                blockGroup.GetBlocksOfType(_lights);
                _gyrophare = _lights.Find(x => x.CustomName.ToLower().Contains("gyro"));
                if (_gyrophare != null) _lights.Remove(_gyrophare);
                _lights.Sort(SortByName);

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
State events: {(_airlockStateMachine.ContainsKey(_status) ? _airlockStateMachine[_status].Count : 0)}
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
                    foreach (KeyValuePair<AirlockEvent, EventLoopTask> eventTask in eventMap.Value)
                    {
                        Func<bool> condition = null;
                        switch (eventTask.Key)
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
                        _eventLookup[eventTask.Key] = el.AddProbe((el2, probe) =>
                        {
                            probe.Disable();
                            el2.ResetTimeout(_timeoutLookup[eventMap.Key]);
                            el2.AddTask(eventTask.Value);
                        }, condition, 100);
                    }
                }
                el.SetInterval(UpdateProbes, 1000);
                _initialized = true;
            }

            private void UpdateProbes(EventLoop el, EventLoopTimer timer)
            {
                foreach (var probe in _eventLookup.Values) probe.Disable();
                Dictionary<AirlockEvent, EventLoopTask> eventMap;
                if (_airlockStateMachine.TryGetValue(_status, out eventMap))
                {
                    foreach (KeyValuePair<AirlockEvent, EventLoopTask> eventTask in eventMap)
                    {
                        if (eventTask.Key == AirlockEvent.DoorOpenTimeout)
                        {
                            if (_timeoutLookup[_status] == null)
                                _timeoutLookup[_status] = el.SetTimeout(OnEventTimeout(eventTask.Value), _doorOpenTimeout);
                        }
                        else if (_eventLookup.ContainsKey(eventTask.Key))
                        {
                            _eventLookup[eventTask.Key].Enable();
                        }
                    }
                }
                
            }

            private EventLoopTimerCallback OnEventTimeout(EventLoopTask task)
            {
                return (el2, _) => {
                    _timeoutLookup[_status] = null;
                    foreach (var probe in _eventLookup.Values) probe.Disable();
                    el2.AddTask(task);
                };
            }

            private IEnumerable<EventLoopTask> TaskCloseExternalDoor(EventLoop el)
            {
                _externalDoors.ForEach(door => door.CloseDoor());
                SetExternalDoorState(AirlockState.ExternalDoorClosing);
                yield return el.WaitFor(ExternalDoorClosed, 100, _errorTimeout);
                if (ExternalDoorClosed())
                    SetExternalDoorState(AirlockState.ExternalDoorClosed);
                else
                    _status |= AirlockState.Error;
            }

            private IEnumerable<EventLoopTask> TaskCloseInternalDoor(EventLoop el)
            {
                _internalDoors.ForEach(door => door.CloseDoor());
                SetInternalDoorState(AirlockState.InternalDoorClosing);
                yield return el.WaitFor(InternalDoorClosed, 100, _errorTimeout);
                if (InternalDoorClosed())
                    SetInternalDoorState(AirlockState.InternalDoorClosed);
                else
                    _status |= AirlockState.Error;
            }

            private IEnumerable<EventLoopTask> TaskOpenExternalDoor(EventLoop el)
            {
                _externalDoors.ForEach(door => door.OpenDoor());
                SetExternalDoorState(AirlockState.ExternalDoorOpening);
                yield return el.WaitFor(ExternalDoorOpen, 100, _errorTimeout);
                if (ExternalDoorOpen())
                    SetExternalDoorState(AirlockState.ExternalDoorOpen);
                else
                    _status |= AirlockState.Error;
            }
            private IEnumerable<EventLoopTask> TaskOpenInternalDoor(EventLoop el)
            {
                _internalDoors.ForEach(door => door.OpenDoor());
                SetInternalDoorState(AirlockState.InternalDoorOpening);
                yield return el.WaitFor(InternalDoorOpen, 100, _errorTimeout);
                if (InternalDoorOpen())
                    SetInternalDoorState(AirlockState.InternalDoorOpen);
                else
                    _status |= AirlockState.Error;
            }
            private IEnumerable<EventLoopTask> TaskWayOut(EventLoop el)
            {
                _status |= AirlockState.ExitCycling;
                yield return TaskCloseInternalDoor;
                if ((_status & AirlockState.Error) != 0) yield break;
                yield return TaskDepressurize;
                if ((_status & AirlockState.Error) != 0) yield break;
                yield return TaskOpenExternalDoor;
                if ((_status & AirlockState.Error) != 0) yield break;
                yield return el.WaitFor(SensorInsideOff, 100, _errorTimeout);
                _status &= ~AirlockState.ExitCycling;
            }

            private IEnumerable<EventLoopTask> TaskWayIn(EventLoop el)
            {
                _status |= AirlockState.EntryCycling;
                yield return TaskCloseExternalDoor;
                if ((_status & AirlockState.Error) != 0) yield break;
                yield return TaskPressurize;
                if ((_status & AirlockState.Error) != 0) yield break;
                yield return TaskOpenInternalDoor;
                if ((_status & AirlockState.Error) != 0) yield break;
                yield return el.WaitFor(SensorInsideOff, 100, _errorTimeout);
                _status &= ~AirlockState.EntryCycling;

            }

            private IEnumerable<EventLoopTask> TaskDepressurize(EventLoop el)
            {
                _airVents.ForEach(airVent => airVent.Depressurize = true);
                if (_gyrophare != null) _gyrophare.Enabled = true;
                SetAirState(AirlockState.AirDepressurizing);
                yield return el.WaitFor(AirVentsDepressurized, 100, _errorTimeout);
                SetAirState(AirlockState.AirDepressurized);
                if (_gyrophare != null) _gyrophare.Enabled = false;
            }

            private IEnumerable<EventLoopTask> TaskPressurize(EventLoop el)
            {
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

            private static int SortByName(IMyTerminalBlock a, IMyTerminalBlock b)
            {
                return a.CustomName.CompareTo(b.CustomName);
            }

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
