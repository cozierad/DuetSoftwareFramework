﻿using System;
using System.Text.Json.Serialization;

namespace DuetAPI.ObjectModel
{
    /// <summary>
    /// Information about the machine state
    /// </summary>
    public sealed class State : ModelObject
    {
        /// <summary>
        /// State of the ATX power pin (if controlled)
        /// </summary>
        public bool? AtxPower
        {
            get => _atxPower;
            set => SetPropertyValue(ref _atxPower, value);
        }
        private bool? _atxPower;

        /// <summary>
        /// Port of the ATX power pin or null if not assigned
        /// </summary>
        public string? AtxPowerPort
        {
            get => _atxPowerPort;
            set => SetPropertyValue(ref _atxPowerPort, value);
        }
        private string? _atxPowerPort;

        /// <summary>
        /// Information about a requested beep or null if none is requested
        /// </summary>
        public BeepRequest? Beep
        {
            get => _beep;
            set => SetPropertyValue(ref _beep, value);
        }
        private BeepRequest? _beep;

        /// <summary>
        /// Number of the currently selected tool or -1 if none is selected
        /// </summary>
        public int CurrentTool
        {
            get => _currentTool;
            set => SetPropertyValue(ref _currentTool, value);
        }
        private int _currentTool = -1;

        /// <summary>
        /// When provided it normally has value 0 normally and 1 when a deferred power down is pending
        /// </summary>
        /// <remarks>
        /// It is only available after power switching has been enabled by M80 or M81
        /// </remarks>
        public bool? DeferredPowerDown
        {
            get => _deferredPowerDown;
            set => SetPropertyValue(ref _deferredPowerDown, value);
        }
        private bool? _deferredPowerDown;

        /// <summary>
        /// Persistent message to display (see M117)
        /// </summary>
        public string DisplayMessage
        {
            get => _displayMessage;
            set => SetPropertyValue(ref _displayMessage, value);
        }
        private string _displayMessage = string.Empty;

        /// <summary>
        /// List of general-purpose output ports
        /// </summary>
        /// <seealso cref="GpOutputPort"/>
        public ModelCollection<GpOutputPort?> GpOut { get; } = new ModelCollection<GpOutputPort?>();

        /// <summary>
        /// Laser PWM of the next commanded move (0..1) or null if not applicable
        /// </summary>
        public float? LaserPwm
        {
            get => _laserPwm;
            set => SetPropertyValue(ref _laserPwm, value);
        }
        private float? _laserPwm;

        /// <summary>
        /// Log file being written to or null if logging is disabled
        /// </summary>
        [SbcProperty(true)]
        public string? LogFile
        {
            get => _logFile;
            set => SetPropertyValue(ref _logFile, value);
        }
        private string? _logFile;

        /// <summary>
        /// Current log level
        /// </summary>
        [SbcProperty(true)]
        public LogLevel LogLevel
        {
            get => _logLevel;
            set => SetPropertyValue(ref _logLevel, value);
        }
        private LogLevel _logLevel = LogLevel.Off;

        /// <summary>
        /// Details about a requested message box or null if none is requested
        /// </summary>
        public MessageBox? MessageBox
        {
            get => _messageBox;
            set => SetPropertyValue(ref _messageBox, value);
        }
        private MessageBox? _messageBox;

        /// <summary>
        /// Current mode of operation
        /// </summary>
        public MachineMode MachineMode
        {
            get => _machineMode;
			set => SetPropertyValue(ref _machineMode, value);
        }
        private MachineMode _machineMode = MachineMode.FFF;

        /// <summary>
        /// Indicates if the current macro file was restarted after a pause
        /// </summary>
        public bool MacroRestarted
        {
            get => _macroRestarted;
            set => SetPropertyValue(ref _macroRestarted, value);
        }
        private bool _macroRestarted;

        /// <summary>
        /// Millisecond fraction of <see cref="UpTime"/>
        /// </summary>
        public int MsUpTime
        {
            get => _msUpTime;
            set => SetPropertyValue(ref _msUpTime, value);
        }
        private int _msUpTime;

        /// <summary>
        /// Number of the next tool to be selected
        /// </summary>
        public int NextTool
        {
            get => _nextTool;
			set => SetPropertyValue(ref _nextTool, value);
        }
        private int _nextTool = -1;

        /// <summary>
        /// Indicates if at least one plugin has been started
        /// </summary>
        [SbcProperty(false)]
        public bool PluginsStarted
        {
            get => _pluginsStarted;
            set => SetPropertyValue(ref _pluginsStarted, value);
        }
        private bool _pluginsStarted;

        /// <summary>
        /// Script to execute when the power fails
        /// </summary>
        public string PowerFailScript
        {
            get => _powerFailScript;
			set => SetPropertyValue(ref _powerFailScript, value);
        }
        private string _powerFailScript = string.Empty;

        /// <summary>
        /// Number of the previous tool
        /// </summary>
        public int PreviousTool
        {
            get => _previousTool;
			set => SetPropertyValue(ref _previousTool, value);
        }
        private int _previousTool = -1;

        /// <summary>
        /// List of restore points
        /// </summary>
        public ModelCollection<RestorePoint> RestorePoints { get; } = new ModelCollection<RestorePoint>();

        /// <summary>
        /// First error on start-up or null if there was none
        /// </summary>
        [SbcProperty(true)]
        public StartupError? StartupError
        {
            get => _startupError;
            set => SetPropertyValue(ref _startupError, value);
        }
        private StartupError? _startupError;

        /// <summary>
        /// Current state of the machine
        /// </summary>
        public MachineStatus Status
        {
            get => _status;
			set => SetPropertyValue(ref _status, value);
        }
        private MachineStatus _status = MachineStatus.Starting;

        /// <summary>
        /// Shorthand for inputs[state.thisInput].active
        /// </summary>
        public bool ThisActive
        {
            get => _thisActive;
            set => SetPropertyValue(ref _thisActive, value);
        }
        private bool _thisActive = true;

        /// <summary>
        /// Index of the current G-code input channel (see <see cref="ObjectModel.Inputs"/>)
        /// </summary>
        /// <remarks>
        /// This is primarily intended for macro files to determine on which G-code channel it is running.
        /// The value of this property is always null in object model queries
        /// </remarks>
        public int? ThisInput
        {
            get => _thisInput;
            set => SetPropertyValue(ref _thisInput, value);
        }
        private int? _thisInput;

        /// <summary>
        /// Internal date and time in RepRapFirmware or null if unknown
        /// </summary>
        [JsonConverter(typeof(Utility.JsonOptionalShortDateTimeConverter))]
        public DateTime? Time
        {
            get => _time;
            set => SetPropertyValue(ref _time, value);
        }
        private DateTime? _time;

        /// <summary>
        /// How long the machine has been running (in s)
        /// </summary>
        public int UpTime
        {
            get => _upTime;
			set => SetPropertyValue(ref _upTime, value);
        }
        private int _upTime;
    }
}