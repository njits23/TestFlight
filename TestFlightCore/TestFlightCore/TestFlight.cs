using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;


using UnityEngine;
using KSPPluginFramework;
using TestFlightAPI;

namespace TestFlightCore
{
    internal struct PartStatus
    {
        internal string partName;
        internal uint partID;
        internal int partStatus;
        internal double flightTime;
        internal double flightData;
        internal double reliability;
        internal double momentaryReliability;
        internal ITestFlightCore flightCore;
        internal ITestFlightFailure activeFailure;
        internal bool highlightPart;
        internal string repairRequirements;
        internal bool acknowledged;
    }

    internal struct MasterStatusItem
    {
        internal Guid vesselID;
        internal string vesselName;
        internal List<PartStatus> allPartsStatus;
    }

    public class PartFlightData : IConfigNode
    {
        private List<TestFlightData> flightData = null;
        private string partName = "";

        public void AddFlightData(string name, TestFlightData data)
        {
            if (flightData == null)
            {
                flightData = new List<TestFlightData>();
                partName = name;
                // add new entry for this scope
                TestFlightData newData = new TestFlightData();
                newData.scope = data.scope;
                newData.flightData = data.flightData;
                newData.flightTime = 0;
                flightData.Add(newData);
            }
            else
            {
                int dataIndex = flightData.FindIndex(s => s.scope == data.scope);
                if (dataIndex >= 0)
                {
                    TestFlightData currentData = flightData[dataIndex];
                    // We only update the data if its higher than what we already have
                    if (data.flightData > currentData.flightData)
                    {
                        currentData.flightData = data.flightData;
                        flightData[dataIndex] = currentData;
                    }
                    // We don't care about flightTime, so set it to 0
                    currentData.flightTime = 0;
                }
                else
                {
                    // add new entry for this scope
                    TestFlightData newData = new TestFlightData();
                    newData.scope = data.scope;
                    newData.flightData = data.flightData;
                    newData.flightTime = 0;
                    flightData.Add(newData);
                }
            }
        }

        public List<TestFlightData> GetFlightData()
        {
            return flightData;
        }

        public string GetPartName()
        {
            return partName;
        }

        public override string ToString()
        {
            string baseString = partName + ":";
            foreach (TestFlightData data in flightData)
            {
                string dataString = String.Format("{0},{1},0", data.scope, data.flightData);
                baseString = baseString + dataString + " ";
            }

            return baseString;
        }

        public static PartFlightData FromString(string str)
        {
            // String format is
            // partName:scope,data,0 scope,data scope,data,0 scope,data,0 
            PartFlightData newData = null;
            if (str.IndexOf(':') > -1)
            {
                newData = new PartFlightData();
                string[] baseString = str.Split(new char[1]{ ':' });
                newData.partName = baseString[0];
                string[] dataStrings = baseString[1].Split(new char[1]{ ' ' });
                foreach (string dataString in dataStrings)
                {
                    if (newData.flightData == null)
                        newData.flightData = new List<TestFlightData>();

                    if (dataString.Trim().Length > 0)
                    {
                        string[] dataMembers = dataString.Split(new char[1]{ ',' });
                        if (dataMembers.Length == 3)
                        {
                            TestFlightData tfData = new TestFlightData();
                            tfData.scope = dataMembers[0];;
                            tfData.flightData = float.Parse(dataMembers[1]);
                            tfData.flightTime = 0;
                            newData.flightData.Add(tfData);
                        }
                    }
                }
            }
            return newData;
        }

        public void Load(ConfigNode node)
        {
            partName = node.GetValue("partName");
            if (node.HasNode("FLIGHTDATA"))
            {
                flightData = new List<TestFlightData>();
                foreach (ConfigNode dataNode in node.GetNodes("FLIGHTDATA"))
                {
                    TestFlightData newData = new TestFlightData();
                    newData.scope = dataNode.GetValue("scope");
                    if (dataNode.HasValue("flightData"))
                        newData.flightData = float.Parse(dataNode.GetValue("flightData"));
                    flightData.Add(newData);
                }
            }
        }

        public void Save(ConfigNode node)
        {
            node.AddValue("partName", partName);
            foreach (TestFlightData data in flightData)
            {
                ConfigNode dataNode = node.AddNode("FLIGHTDATA");
                dataNode.AddValue("scope", data.scope);
                dataNode.AddValue("flightData", data.flightData);
            }
        }


    }
    [KSPAddon(KSPAddon.Startup.Flight, false)]
    public class TestFlightManager : MonoBehaviourExtended
    {
        internal TestFlightManagerScenario tfScenario = null;
        public static TestFlightManager Instance = null;
        internal bool isReady = false;

        public Dictionary<Guid, double> knownVessels;

        public double pollingInterval = 5.0f;
        public bool processInactiveVessels = true;

        private Dictionary<Guid, MasterStatusItem> masterStatus = null;

        double currentUTC = 0.0;
        double lastDataPoll = 0.0;
        double lastFailurePoll = 0.0;
        double lastMasterStatusUpdate = 0.0;


        internal override void Start()
        {
            base.Start();
            StartCoroutine("ConnectToScenario");
        }

        IEnumerator ConnectToScenario()
        {
            while (TestFlightManagerScenario.Instance == null)
            {
                yield return null;
            }

            tfScenario = TestFlightManagerScenario.Instance;
            while (!tfScenario.isReady)
            {
                yield return null;
            }
            Startup();
        }

        public void Startup()
        {
            isReady = true;
            Instance = this;
        }

        internal Dictionary<Guid, MasterStatusItem> GetMasterStatus()
        {
            return masterStatus;
        }

        private void InitializeParts(Vessel vessel)
        {
            LogFormatted_DebugOnly("TestFlightManager: Initializing parts for vessel " + vessel.GetName());
            foreach (Part part in vessel.parts)
            {
                foreach (PartModule pm in part.Modules)
                {
                    ITestFlightCore core = pm as ITestFlightCore;
                    if (core != null)
                    {
                        PartFlightData partData = tfScenario.GetFlightDataForPartName(pm.part.name);
                        if (partData == null)
                        {
                            partData = new PartFlightData();
                        }

                        if (partData != null)
                        {
                            core.InitializeFlightData(partData.GetFlightData(), tfScenario.settings.globalReliabilityModifier);
                        }
                    }
                }
            }
        }

        // This method simply scans through the Master Status list every now and then and removes vessels and parts that no longer exist
        public void VerifyMasterStatus()
        {
            // iterate through our cached vessels and delete ones that are no longer valid
            List<Guid> vesselsToDelete = new List<Guid>();
            foreach(var entry in masterStatus)
            {
                Vessel vessel = FlightGlobals.Vessels.Find(v => v.id == entry.Key);
                if (vessel == null)
                {
                    LogFormatted_DebugOnly("TestFlightManager: Vessel no longer exists. Marking it for deletion.");
                    vesselsToDelete.Add(entry.Key);
                }
                else
                {
                    if (vessel.vesselType == VesselType.Debris)
                    {
                        LogFormatted_DebugOnly("TestFlightManager: Vessel appears to be debris now. Marking it for deletion.");
                        vesselsToDelete.Add(entry.Key);
                    }
                }
            }
            if (vesselsToDelete.Count > 0)
                LogFormatted_DebugOnly("TestFlightManager: Removing " + vesselsToDelete.Count() + " vessels from Master Status");
            foreach (Guid id in vesselsToDelete)
            {
                masterStatus.Remove(id);
            }
            // iterate through the remaining vessels and check for parts that no longer exist
            List<PartStatus> partsToDelete = new List<PartStatus>();
            foreach (var entry in masterStatus)
            {
                partsToDelete.Clear();
                Vessel vessel = FlightGlobals.Vessels.Find(v => v.id == entry.Key);
                foreach (PartStatus partStatus in masterStatus[entry.Key].allPartsStatus)
                {
                    Part part = vessel.Parts.Find(p => p.flightID == partStatus.partID);
                    if (part == null)
                    {
                        LogFormatted_DebugOnly("TestFlightManager: Could not find part. " + partStatus.partName + "(" + partStatus.partID + ") Marking it for deletion.");
                        partsToDelete.Add(partStatus);
                    }
                }
                if (partsToDelete.Count > 0)
                    LogFormatted_DebugOnly("TestFlightManager: Deleting " + partsToDelete.Count() + " parts from vessel " + vessel.GetName());
                foreach (PartStatus oldPartStatus in partsToDelete)
                {
                    masterStatus[entry.Key].allPartsStatus.Remove(oldPartStatus);
                }
            }
        }

        public void CacheVessels()
        {
            // build a list of vessels to process based on setting
            if (knownVessels == null)
                knownVessels = new Dictionary<Guid, double>();

            // iterate through our cached vessels and delete ones that are no longer valid
            List<Guid> vesselsToDelete = new List<Guid>();
            foreach(var entry in knownVessels)
            {
                Vessel vessel = FlightGlobals.Vessels.Find(v => v.id == entry.Key);
                if (vessel == null)
                    vesselsToDelete.Add(entry.Key);
                else
                {
                    if (vessel.vesselType == VesselType.Debris)
                        vesselsToDelete.Add(entry.Key);
                }
            }
            if (vesselsToDelete.Count() > 0)
                LogFormatted_DebugOnly("TestFlightManager: Deleting " + vesselsToDelete.Count() + " vessels from cached vessels");
            foreach (Guid id in vesselsToDelete)
            {
                knownVessels.Remove(id);
            }

            // Build our cached list of vessels.  The reason we do this is so that we can store an internal "missionStartTime" for each vessel because the game
            // doesn't consider a vessel launched, and does not start the mission clock, until the player activates the first stage.  This is fine except it
            // makes things like engine test stands impossible, so we instead cache the vessel the first time we see it and use that time as the missionStartTime

            if (!tfScenario.settings.processAllVessels)
            {
                if (FlightGlobals.ActiveVessel != null && !knownVessels.ContainsKey(FlightGlobals.ActiveVessel.id))
                {
                    LogFormatted_DebugOnly("TestFlightManager: Adding new vessel " + FlightGlobals.ActiveVessel.GetName() + " with launch time " + Planetarium.GetUniversalTime());
                    knownVessels.Add(FlightGlobals.ActiveVessel.id, Planetarium.GetUniversalTime());
                    InitializeParts(FlightGlobals.ActiveVessel);
                }
            }
            else
            {
                foreach (Vessel vessel in FlightGlobals.Vessels)
                {
                    if (vessel.vesselType == VesselType.Lander || vessel.vesselType == VesselType.Probe || vessel.vesselType == VesselType.Rover || vessel.vesselType == VesselType.Ship || vessel.vesselType == VesselType.Station)
                    {
                        if ( !knownVessels.ContainsKey(vessel.id) )
                        {
                            LogFormatted_DebugOnly("TestFlightManager: Adding new vessel " + vessel.GetName() + " with launch time " + Planetarium.GetUniversalTime());
                            knownVessels.Add(vessel.id, Planetarium.GetUniversalTime());
                            InitializeParts(vessel);
                        }
                    }
                }
            }
        }

        internal override void Update()
        {

            if (masterStatus == null)
                masterStatus = new Dictionary<Guid, MasterStatusItem>();

            currentUTC = Planetarium.GetUniversalTime();
            // ensure out vessel list is up to date
            CacheVessels();
            if (currentUTC >= lastMasterStatusUpdate + tfScenario.settings.masterStatusUpdateFrequency)
            {
                lastMasterStatusUpdate = currentUTC;
                VerifyMasterStatus();
            }
            // process vessels
            foreach (var entry in knownVessels)
            {
                Vessel vessel = FlightGlobals.Vessels.Find(v => v.id == entry.Key);
                if (vessel.loaded)
                {
                    foreach(Part part in vessel.parts)
                    {
                        foreach (PartModule pm in part.Modules)
                        {
                            ITestFlightCore core = pm as ITestFlightCore;
                            if (core != null)
                            {
                                // Poll for flight data and part status
                                if (true) //currentUTC >= lastDataPoll + tfScenario.settings.masterStatusUpdateFrequency
                                {
                                    TestFlightData currentFlightData = new TestFlightData();
                                    currentFlightData.scope = core.GetScope();
                                    LogFormatted_DebugOnly("Getting flight data for " + core.GetScope() + ": " + core.GetFlightData());
                                    currentFlightData.flightData = core.GetFlightData();
                                    currentFlightData.flightTime = core.GetFlightTime();

                                    PartStatus partStatus = new PartStatus();
                                    partStatus.flightCore = core;
                                    partStatus.partName = part.partInfo.title;
                                    partStatus.partID = part.flightID;
                                    partStatus.flightData = currentFlightData.flightData;
                                    partStatus.flightTime = currentFlightData.flightTime;
                                    partStatus.partStatus = core.GetPartStatus();
                                    partStatus.reliability = core.GetCurrentReliability(tfScenario.settings.globalReliabilityModifier);
                                    partStatus.repairRequirements = core.GetRequirementsTooltip();
                                    partStatus.acknowledged = core.IsFailureAcknowledged();
                                    if (core.GetPartStatus() > 0)
                                    {
                                        partStatus.activeFailure = core.GetFailureModule();
                                    }
                                    else
                                    {
                                        partStatus.activeFailure = null;
                                    }

                                    // Update or Add part status in Master Status
                                    if (masterStatus.ContainsKey(vessel.id))
                                    {
                                        // Vessel is already in the Master Status, so check if part is in there as well
                                        int numItems = masterStatus[vessel.id].allPartsStatus.Count(p => p.partID == part.flightID);
                                        int existingPartIndex;
                                        if (numItems == 1)
                                        {
                                            existingPartIndex = masterStatus[vessel.id].allPartsStatus.FindIndex(p => p.partID == part.flightID);
                                            masterStatus[vessel.id].allPartsStatus[existingPartIndex] = partStatus;
                                        }
                                        else if (numItems == 0)
                                        {
                                            LogFormatted_DebugOnly("Adding new part to MSD");
                                            masterStatus[vessel.id].allPartsStatus.Add(partStatus);
                                        }
                                        else
                                        {
                                            existingPartIndex = masterStatus[vessel.id].allPartsStatus.FindIndex(p => p.partID == part.flightID);
                                            masterStatus[vessel.id].allPartsStatus[existingPartIndex] = partStatus;
                                            LogFormatted_DebugOnly("[ERROR] TestFlightManager: Found " + numItems + " matching parts in Master Status Display!");
                                        }
                                    }
                                    else
                                    {
                                        // Vessel is not in the Master Status so create a new entry for it and add this part
                                        LogFormatted_DebugOnly("Adding new vessel and part to MSD");
                                        MasterStatusItem masterStatusItem = new MasterStatusItem();
                                        masterStatusItem.vesselID = vessel.id;
                                        masterStatusItem.vesselName = vessel.GetName();
                                        masterStatusItem.allPartsStatus = new List<PartStatus>();
                                        masterStatusItem.allPartsStatus.Add(partStatus);
                                        masterStatus.Add(vessel.id, masterStatusItem);
                                    }

                                    PartFlightData data = tfScenario.GetFlightDataForPartName(part.name);
                                    if (data != null)
                                    {
                                        data.AddFlightData(part.name, currentFlightData);
                                    }
                                    else
                                    {
                                        data = new PartFlightData();
                                        data.AddFlightData(part.name, currentFlightData);
                                        tfScenario.SetFlightDataForPartName(part.name, data);
                                    }
                                }
                            }
                        }
                    }
                }
                if (currentUTC >= lastDataPoll + tfScenario.settings.minTimeBetweenDataPoll)
                {
                    lastDataPoll = currentUTC;
                }
                if (currentUTC >= lastFailurePoll + tfScenario.settings.minTimeBetweenFailurePoll)
                {
                    lastFailurePoll = currentUTC;
                }
            }
        }
    
    }


    [KSPScenario(ScenarioCreationOptions.AddToAllGames, 
        new GameScenes[] 
        { 
            GameScenes.FLIGHT,
            GameScenes.EDITOR
        }
    )]
	public class TestFlightManagerScenario : ScenarioModule
	{
        internal Settings settings = null;
        public static TestFlightManagerScenario Instance { get; private set; }
        public bool isReady = false;

        public List<PartFlightData> partsFlightData;
        public List<String> partsPackedStrings;

        public override void OnAwake()
        {
            Instance = this;
            if (settings == null)
            {
                settings = new Settings("../settings.cfg");
            }
            settings.Load();
            if (partsFlightData == null)
            {
                partsFlightData = new List<PartFlightData>();
                if (partsPackedStrings != null)
                {
                    foreach (string packedString in partsPackedStrings)
                    {
                        Debug.Log(packedString);
                        PartFlightData data = PartFlightData.FromString(packedString);
                        partsFlightData.Add(data);
                    }
                }
            }
            if (partsPackedStrings == null)
            {
                partsPackedStrings = new List<string>();
            }
            base.OnAwake();
        }

        public void Start()
        {
            Debug.Log("Scenario Start");
            isReady = true;
        }

        public PartFlightData GetFlightDataForPartName(string partName)
        {
            foreach (PartFlightData data in partsFlightData)
            {
                if (data.GetPartName() == partName)
                    return data;
            }
            return null;
        }

        public void SetFlightDataForPartName(string partName, PartFlightData data)
        {
            int index = partsFlightData.FindIndex(pfd => pfd.GetPartName() == partName);
            if (index == -1)
                partsFlightData.Add(data);
            else
                partsFlightData[index] = data;
        }

        public override void OnLoad(ConfigNode node)
        {
            base.OnLoad(node);
            if (settings != null)
            {
                settings.Load();
            }
            if (node.HasNode("FLIGHTDATA_PART"))
            {
                if (partsFlightData == null)
                    partsFlightData = new List<PartFlightData>();

                foreach (ConfigNode partNode in node.GetNodes("FLIGHTDATA_PART"))
                {
                    PartFlightData partData = new PartFlightData();
                    partData.Load(partNode);
                    partsFlightData.Add(partData);
                    partsPackedStrings.Add(partData.ToString());
                }
            }
        }

		public override void OnSave(ConfigNode node)
		{
            base.OnSave(node);
            if (settings != null)
            {
                settings.Save();
            }
            foreach (PartFlightData partData in partsFlightData)
            {
                ConfigNode partNode = node.AddNode("FLIGHTDATA_PART");
                partData.Save(partNode);
            }
		}
	}
}