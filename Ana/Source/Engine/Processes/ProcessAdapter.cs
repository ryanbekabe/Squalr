﻿namespace Ana.Source.Engine.Processes
{
    using OperatingSystems.Windows.Native;
    using Output;
    using System;
    using System.Collections.Generic;
    using System.Diagnostics;
    using System.Drawing;
    using System.Linq;
    using System.Runtime.InteropServices;
    using Utils.DataStructures;
    /// <summary>
    /// A class responsible for collecting all running processes on the system.
    /// </summary>
    internal class ProcessAdapter : IProcesses
    {
        /// <summary>
        /// Thread safe collection of listeners.
        /// </summary>
        private ConcurrentHashSet<IProcessObserver> processListeners;

        /// <summary>
        /// Initializes a new instance of the <see cref="ProcessAdapter" /> class.
        /// </summary>
        public ProcessAdapter()
        {
            this.processListeners = new ConcurrentHashSet<IProcessObserver>();
        }

        /// <summary>
        /// Gets or sets the the opened process.
        /// </summary>
        private NormalizedProcess OpenedProcess { get; set; }

        /// <summary>
        /// Subscribes the listener to process change events.
        /// </summary>
        /// <param name="listener">The object that wants to listen to process update events.</param>
        public void Subscribe(IProcessObserver listener)
        {
            this.processListeners.Add(listener);
        }

        /// <summary>
        /// Unsubscribes the listener from process change events.
        /// </summary>
        /// <param name="listener">The object that wants to listen to process update events.</param>
        public void Unsubscribe(IProcessObserver listener)
        {
            this.processListeners?.Remove(listener);
        }

        /// <summary>
        /// Gets all running processes on the system.
        /// </summary>
        /// <returns>An enumeration of see <see cref="NormalizedProcess" />.</returns>
        public IEnumerable<NormalizedProcess> GetProcesses()
        {
            return Process.GetProcesses()
                .Select(externalProcess => new IntermediateProcess(
                    this.IsProcessSystemProcess(externalProcess),
                    this.isProcessWindowed(externalProcess),
                    externalProcess))
                .Select(intermediateProcess => new NormalizedProcess(
                        intermediateProcess.ExternalProcess.Id,
                        intermediateProcess.ExternalProcess.ProcessName,
                        intermediateProcess.IsSystemProcess ? DateTime.MinValue : intermediateProcess.ExternalProcess.StartTime,
                        intermediateProcess.IsSystemProcess,
                        intermediateProcess.HasWindow,
                        this.GetIcon(intermediateProcess)))
                .OrderByDescending(normalizedProcess => normalizedProcess.StartTime);
        }

        /// <summary>
        /// Opens a process for editing.
        /// </summary>
        /// <param name="process">The process to be opened.</param>
        public void OpenProcess(NormalizedProcess process)
        {
            this.OpenedProcess = process;

            if (process != null)
            {
                OutputViewModel.GetInstance().Log(OutputViewModel.LogLevel.Info, "Attached to process: " + process.ProcessName);
            }
            else
            {
                OutputViewModel.GetInstance().Log(OutputViewModel.LogLevel.Warn, "Detached from target process");
            }

            if (this.processListeners != null)
            {
                foreach (IProcessObserver listener in this.processListeners)
                {
                    listener.Update(process);
                }
            }
        }

        /// <summary>
        /// Gets the process that has been opened.
        /// </summary>
        /// <returns>The opened process.</returns>
        public NormalizedProcess GetOpenedProcess()
        {
            return this.OpenedProcess;
        }

        /// <summary>
        /// Determines if the opened process is 32 bit.
        /// </summary>
        /// <returns>Returns true if the opened process is 32 bit, otherwise false.</returns>
        public Boolean IsOpenedProcess32Bit()
        {
            // First do the simple check if seeing if the OS is 32 bit, in which case the process wont be 64 bit
            if (EngineCore.GetInstance().OperatingSystemAdapter.IsOperatingSystem32Bit())
            {
                return true;
            }

            return EngineCore.GetInstance().OperatingSystemAdapter.IsProcess32Bit(this.OpenedProcess);
        }

        /// <summary>
        /// Determines if the opened process is 64 bit.
        /// </summary>
        /// <returns>Returns true if the opened process is 64 bit, otherwise false.</returns>
        public Boolean IsOpenedProcess64Bit()
        {
            return !this.IsOpenedProcess32Bit();
        }

        /// <summary>
        /// Determines if the provided process is a system process.
        /// </summary>
        /// <param name="externalProcess">The process to check.</param>
        /// <returns>A value indicating whether or not the given process is a system process.</returns>
        private Boolean IsProcessSystemProcess(Process externalProcess)
        {
            if (externalProcess.SessionId == 0 || externalProcess.BasePriority == 13)
            {
                return true;
            }

            try
            {
                if (externalProcess.PriorityBoostEnabled)
                {
                    // Accessing this field will cause an access exception for system processes. This saves
                    // time because handling the exception is faster than failing to fetch the icon later
                    return false;
                }
            }
            catch
            {
                return true;
            }

            return false;
        }

        /// <summary>
        /// Determines if a process has a window.
        /// </summary>
        /// <param name="externalProcess">The process to check.</param>
        /// <returns>A value indicating whether or not the given process has a window.</returns>
        private Boolean isProcessWindowed(Process externalProcess)
        {
            // Step 1: Check if there is a window handle
            if (externalProcess.MainWindowHandle != IntPtr.Zero)
            {
                return true;
            }

            // Step 2: Enumerate threads, looking for window threads that reference visible windows
            foreach (ProcessThread threadInfo in externalProcess.Threads)
            {
                IntPtr[] windows = GetWindowHandlesForThread(threadInfo.Id);

                if (windows != null)
                {
                    foreach (IntPtr handle in windows)
                    {
                        if (IsWindowVisible(handle))
                        {
                            return true;
                        }
                    }
                }
            }

            return false;
        }

        private IntPtr[] GetWindowHandlesForThread(Int32 threadHandle)
        {
            results.Clear();
            EnumWindows(WindowEnum, threadHandle);

            return results.ToArray();
        }

        private delegate Int32 EnumWindowsProc(IntPtr hwnd, Int32 lParam);

        [DllImport("user32")]
        private static extern Int32 EnumWindows(EnumWindowsProc x, Int32 y);
        [DllImport("user32")]
        public static extern Int32 GetWindowThreadProcessId(IntPtr handle, out Int32 processId);
        [DllImport("user32")]
        static extern Boolean IsWindowVisible(IntPtr hWnd);

        private List<IntPtr> results = new List<IntPtr>();

        private Int32 WindowEnum(IntPtr hWnd, Int32 lParam)
        {
            Int32 processID = 0;
            Int32 threadID = GetWindowThreadProcessId(hWnd, out processID);
            if (threadID == lParam)
            {
                results.Add(hWnd);
            }

            return 1;
        }

        /// <summary>
        /// Fetches the icon associated with the provided process.
        /// </summary>
        /// <param name="intermediateProcess">An intermediate process structure.</param>
        /// <returns>An Icon associated with the given process. Returns null if there is no icon.</returns>
        private Icon GetIcon(IntermediateProcess intermediateProcess)
        {
            const Icon NoIcon = null;

            if (intermediateProcess.IsSystemProcess)
            {
                return NoIcon;
            }

            try
            {
                // TODO: This is a violation of the abstraction of native methods into just the OS adaptor. Either all process functions go into the OS Adapter,
                // or this portion must be moved into the Windows Adapter
                IntPtr iconHandle = NativeMethods.ExtractIcon(intermediateProcess.ExternalProcess.Handle, intermediateProcess.ExternalProcess.MainModule.FileName, 0);

                if (iconHandle == IntPtr.Zero)
                {
                    return NoIcon;
                }

                return Icon.FromHandle(iconHandle);
            }
            catch
            {
                return NoIcon;
            }
        }

        /// <summary>
        /// Temporary structure used in collecting all running processes.
        /// </summary>
        private struct IntermediateProcess
        {
            /// <summary>
            /// Initializes a new instance of the <see cref="IntermediateProcess" /> struct.
            /// </summary>
            /// <param name="isSystemProcess">Whether or not the process is a system process.</param>
            /// <param name="externalProcess">The external process.</param>
            public IntermediateProcess(Boolean isSystemProcess, Boolean hasWindow, Process externalProcess)
            {
                this.IsSystemProcess = isSystemProcess;
                this.HasWindow = hasWindow;
                this.ExternalProcess = externalProcess;
            }

            /// <summary>
            /// Gets a value indicating whether or not the process is a system process.
            /// </summary>
            public Boolean IsSystemProcess { get; private set; }

            /// <summary>
            /// Gets a value indicating whether or not the process has a window.
            /// </summary>
            public Boolean HasWindow { get; private set; }

            /// <summary>
            /// Gets the process associated with this intermediate structure.
            /// </summary>
            public Process ExternalProcess { get; private set; }
        }
    }
    //// End class
}
//// End namespace