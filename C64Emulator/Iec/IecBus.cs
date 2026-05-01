/*
   Copyright 2026 Nils Kopal <Nils.Kopal<at>kopaldev.de

   Licensed under the Apache License, Version 2.0 (the "License");
   you may not use this file except in compliance with the License.
   You may obtain a copy of the License at

       http://www.apache.org/licenses/LICENSE-2.0

   Unless required by applicable law or agreed to in writing, software
   distributed under the License is distributed on an "AS IS" BASIS,
   WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
   See the License for the specific language governing permissions and
   limitations under the License.
*/
using System;
using System.Collections.Generic;

namespace C64Emulator.Core
{
    /// <summary>
    /// Lists the supported iec bus line values.
    /// </summary>
    public enum IecBusLine
    {
        Atn,
        Clock,
        Data,
        ServiceRequest
    }

    /// <summary>
    /// Represents the iec bus component.
    /// </summary>
    public sealed class IecBus
    {
        private readonly List<Action<IecBusLine, bool>> _lineChangeListeners =
            new List<Action<IecBusLine, bool>>();

        /// <summary>
        /// Represents the participant state component.
        /// </summary>
        private sealed class ParticipantState
        {
            public bool AtnLow;
            public bool ClockLow;
            public bool DataLow;
            public bool ServiceRequestLow;
        }

        private readonly Dictionary<string, ParticipantState> _participants =
            new Dictionary<string, ParticipantState>(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// Creates port.
        /// </summary>
        public IecBusPort CreatePort(string ownerName)
        {
            if (string.IsNullOrWhiteSpace(ownerName))
            {
                throw new ArgumentException("Owner name is required.", nameof(ownerName));
            }

            if (_participants.ContainsKey(ownerName))
            {
                throw new InvalidOperationException("An IEC bus participant with the same name already exists.");
            }

            _participants.Add(ownerName, new ParticipantState());
            return new IecBusPort(this, ownerName);
        }

        /// <summary>
        /// Returns whether line low is true.
        /// </summary>
        public bool IsLineLow(IecBusLine line)
        {
            foreach (ParticipantState participant in _participants.Values)
            {
                switch (line)
                {
                    case IecBusLine.Atn:
                        if (participant.AtnLow)
                        {
                            return true;
                        }

                        break;
                    case IecBusLine.Clock:
                        if (participant.ClockLow)
                        {
                            return true;
                        }

                        break;
                    case IecBusLine.Data:
                        if (participant.DataLow)
                        {
                            return true;
                        }

                        break;
                    case IecBusLine.ServiceRequest:
                        if (participant.ServiceRequestLow)
                        {
                            return true;
                        }

                        break;
                }
            }

            return false;
        }

        /// <summary>
        /// Returns whether line high is true.
        /// </summary>
        public bool IsLineHigh(IecBusLine line)
        {
            return !IsLineLow(line);
        }

        /// <summary>
        /// Handles the register line change listener operation.
        /// </summary>
        public void RegisterLineChangeListener(Action<IecBusLine, bool> listener)
        {
            if (listener == null)
            {
                throw new ArgumentNullException(nameof(listener));
            }

            _lineChangeListeners.Add(listener);
        }

        /// <summary>
        /// Gets the line owners debug value.
        /// </summary>
        public string GetLineOwnersDebug(IecBusLine line)
        {
            var owners = new List<string>();
            foreach (KeyValuePair<string, ParticipantState> entry in _participants)
            {
                bool low = false;
                switch (line)
                {
                    case IecBusLine.Atn:
                        low = entry.Value.AtnLow;
                        break;
                    case IecBusLine.Clock:
                        low = entry.Value.ClockLow;
                        break;
                    case IecBusLine.Data:
                        low = entry.Value.DataLow;
                        break;
                    case IecBusLine.ServiceRequest:
                        low = entry.Value.ServiceRequestLow;
                        break;
                }

                if (low)
                {
                    owners.Add(entry.Key);
                }
            }

            return owners.Count == 0 ? "-" : string.Join(",", owners);
        }

        /// <summary>
        /// Returns whether owner driving line low is true.
        /// </summary>
        internal bool IsOwnerDrivingLineLow(string ownerName, IecBusLine line)
        {
            ParticipantState participant;
            if (!_participants.TryGetValue(ownerName, out participant))
            {
                return false;
            }

            switch (line)
            {
                case IecBusLine.Atn:
                    return participant.AtnLow;
                case IecBusLine.Clock:
                    return participant.ClockLow;
                case IecBusLine.Data:
                    return participant.DataLow;
                case IecBusLine.ServiceRequest:
                    return participant.ServiceRequestLow;
                default:
                    return false;
            }
        }

        /// <summary>
        /// Sets the line state value.
        /// </summary>
        internal void SetLineState(string ownerName, IecBusLine line, bool driveLow)
        {
            ParticipantState participant;
            if (!_participants.TryGetValue(ownerName, out participant))
            {
                throw new InvalidOperationException("Unknown IEC bus participant.");
            }

            bool wasLow = IsLineLow(line);

            switch (line)
            {
                case IecBusLine.Atn:
                    participant.AtnLow = driveLow;
                    break;
                case IecBusLine.Clock:
                    participant.ClockLow = driveLow;
                    break;
                case IecBusLine.Data:
                    participant.DataLow = driveLow;
                    break;
                case IecBusLine.ServiceRequest:
                    participant.ServiceRequestLow = driveLow;
                    break;
            }

            bool isLow = IsLineLow(line);
            if (wasLow != isLow)
            {
                NotifyLineChanged(line, isLow);
            }
        }

        /// <summary>
        /// Sets the line states value.
        /// </summary>
        internal void SetLineStates(
            string ownerName,
            bool? atnLow = null,
            bool? clockLow = null,
            bool? dataLow = null,
            bool? serviceRequestLow = null)
        {
            ParticipantState participant;
            if (!_participants.TryGetValue(ownerName, out participant))
            {
                throw new InvalidOperationException("Unknown IEC bus participant.");
            }

            bool oldAtnLow = IsLineLow(IecBusLine.Atn);
            bool oldClockLow = IsLineLow(IecBusLine.Clock);
            bool oldDataLow = IsLineLow(IecBusLine.Data);
            bool oldServiceRequestLow = IsLineLow(IecBusLine.ServiceRequest);

            if (atnLow.HasValue)
            {
                participant.AtnLow = atnLow.Value;
            }

            if (clockLow.HasValue)
            {
                participant.ClockLow = clockLow.Value;
            }

            if (dataLow.HasValue)
            {
                participant.DataLow = dataLow.Value;
            }

            if (serviceRequestLow.HasValue)
            {
                participant.ServiceRequestLow = serviceRequestLow.Value;
            }

            NotifyIfChanged(IecBusLine.Atn, oldAtnLow);
            NotifyIfChanged(IecBusLine.Clock, oldClockLow);
            NotifyIfChanged(IecBusLine.Data, oldDataLow);
            NotifyIfChanged(IecBusLine.ServiceRequest, oldServiceRequestLow);
        }

        /// <summary>
        /// Handles the notify if changed operation.
        /// </summary>
        private void NotifyIfChanged(IecBusLine line, bool oldIsLow)
        {
            bool isLow = IsLineLow(line);
            if (oldIsLow != isLow)
            {
                NotifyLineChanged(line, isLow);
            }
        }

        /// <summary>
        /// Handles the notify line changed operation.
        /// </summary>
        private void NotifyLineChanged(IecBusLine line, bool isLow)
        {
            if (_lineChangeListeners.Count == 0)
            {
                return;
            }

            Action<IecBusLine, bool>[] listeners = _lineChangeListeners.ToArray();
            for (int index = 0; index < listeners.Length; index++)
            {
                listeners[index](line, isLow);
            }
        }
    }

    /// <summary>
    /// Represents the iec bus port component.
    /// </summary>
    public sealed class IecBusPort
    {
        private readonly IecBus _bus;
        private readonly string _ownerName;

        /// <summary>
        /// Initializes a new IecBusPort instance.
        /// </summary>
        internal IecBusPort(IecBus bus, string ownerName)
        {
            _bus = bus;
            _ownerName = ownerName;
        }

        /// <summary>
        /// Sets the line low value.
        /// </summary>
        public void SetLineLow(IecBusLine line, bool driveLow)
        {
            _bus.SetLineState(_ownerName, line, driveLow);
        }

        /// <summary>
        /// Returns whether line low is true.
        /// </summary>
        public bool IsLineLow(IecBusLine line)
        {
            return _bus.IsLineLow(line);
        }

        /// <summary>
        /// Returns whether line high is true.
        /// </summary>
        public bool IsLineHigh(IecBusLine line)
        {
            return _bus.IsLineHigh(line);
        }

        /// <summary>
        /// Handles the register line change listener operation.
        /// </summary>
        public void RegisterLineChangeListener(Action<IecBusLine, bool> listener)
        {
            _bus.RegisterLineChangeListener(listener);
        }

        /// <summary>
        /// Returns whether owner driving line low is true.
        /// </summary>
        public bool IsOwnerDrivingLineLow(string ownerName, IecBusLine line)
        {
            return _bus.IsOwnerDrivingLineLow(ownerName, line);
        }

        /// <summary>
        /// Sets the lines value.
        /// </summary>
        public void SetLines(
            bool? atnLow = null,
            bool? clockLow = null,
            bool? dataLow = null,
            bool? serviceRequestLow = null)
        {
            _bus.SetLineStates(_ownerName, atnLow, clockLow, dataLow, serviceRequestLow);
        }
    }
}
