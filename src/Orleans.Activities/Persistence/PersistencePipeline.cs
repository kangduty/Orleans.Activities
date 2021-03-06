﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

using System.Runtime.DurableInstancing;
using System.Xml.Linq;
using Orleans.Activities.AsyncEx;
using Orleans.Activities.Helpers;

namespace Orleans.Activities.Persistence
{
    /// <summary>
    /// Handles the processing of the extensions that implement IPersistenceParticipant, IPersistenceIOParticipant interface,
    /// or descendant of the System.Activities.Persistence.PersistenceParticipant.
    /// </summary>
    public class PersistencePipeline
    {
        // Yes, IEnumerable<object> is ugly, but there is nothing common in IPersistenceParticipant and PersistenceParticipant.
        private readonly IEnumerable<object> persistenceParticipants;
        private readonly IDictionary<XName, InstanceValue> instanceValues;
        private readonly bool persistWriteOnlyValues;

        private readonly ValueDictionaryView readWriteView;
        private readonly ValueDictionaryView writeOnlyView;

        // Used for the save pipeline.
        public PersistencePipeline(IEnumerable<object> persistenceParticipants, IDictionary<XName, InstanceValue> instanceValues, bool persistWriteOnlyValues)
        {
            this.persistenceParticipants = persistenceParticipants;
            this.instanceValues = instanceValues;
            this.persistWriteOnlyValues = persistWriteOnlyValues;
            readWriteView = new ValueDictionaryView(this.instanceValues, false);
            writeOnlyView = new ValueDictionaryView(this.instanceValues, true);
        }

        // Used for the load pipeline.
        public PersistencePipeline(IEnumerable<object> persistenceParticipants, IDictionary<XName, InstanceValue> instanceValues)
        {
            this.persistenceParticipants = persistenceParticipants;
            this.instanceValues = instanceValues;
            //this.persistWriteOnlyValues = false;
            readWriteView = new ValueDictionaryView(this.instanceValues, false);
            //writeOnlyView = null;
        }

        public void Collect()
        {
            foreach (object persistenceParticipant in persistenceParticipants)
            {
                IDictionary<XName, object> readWriteValues = null;
                IDictionary<XName, object> writeOnlyValues = null;

                if (persistenceParticipant is System.Activities.Persistence.PersistenceParticipant legacyPersistenceParticipant)
                    legacyPersistenceParticipant.CollectValues(out readWriteValues, out writeOnlyValues);
                else
                    (persistenceParticipant as IPersistenceParticipant)?.CollectValues(out readWriteValues, out writeOnlyValues);

                if (readWriteValues != null)
                    foreach (KeyValuePair<XName, object> value in readWriteValues)
                        try
                        {
                            instanceValues.Add(value.Key, new InstanceValue(value.Value));
                        }
                        catch (ArgumentException exception)
                        {
                            throw new InvalidOperationException($"Name collision on key '{value.Key}' during collect in extension '{persistenceParticipant.GetType().GetFriendlyName()}'.", exception);
                        }
                if (persistWriteOnlyValues && writeOnlyValues != null)
                    foreach (KeyValuePair<XName, object> value in writeOnlyValues)
                        try
                        {
                            instanceValues.Add(value.Key, new InstanceValue(value.Value, InstanceValueOptions.WriteOnly | InstanceValueOptions.Optional));
                        }
                        catch (ArgumentException exception)
                        {
                            throw new InvalidOperationException($"Name collision on key '{value.Key}' during collect in extension '{persistenceParticipant.GetType().GetFriendlyName()}'.", exception);
                        }
            }
        }

        public void Map()
        {
            List<Tuple<object, IDictionary<XName, object>>> pendingValues = null;

            foreach (object persistenceParticipant in persistenceParticipants)
            {
                IDictionary<XName, object> mappedValues = null;

                if (persistenceParticipant is System.Activities.Persistence.PersistenceParticipant legacyPersistenceParticipant)
                    mappedValues = legacyPersistenceParticipant.MapValues(readWriteView, writeOnlyView);
                else
                    mappedValues = (persistenceParticipant as IPersistenceParticipant)?.MapValues(readWriteView, writeOnlyView);

                if (mappedValues != null)
                {
                    if (pendingValues == null)
                        pendingValues = new List<Tuple<object, IDictionary<XName, object>>>();
                    pendingValues.Add(new Tuple<object, IDictionary<XName, object>>(persistenceParticipant, mappedValues));
                }
            }

            if (pendingValues != null)
            {
                foreach (Tuple<object, IDictionary<XName, object>> writeOnlyValues in pendingValues)
                    foreach (KeyValuePair<XName, object> value in writeOnlyValues.Item2)
                        try
                        {
                            instanceValues.Add(value.Key, new InstanceValue(value.Value, InstanceValueOptions.WriteOnly | InstanceValueOptions.Optional));
                        }
                        catch (ArgumentException exception)
                        {
                            throw new InvalidOperationException($"Name collision on key '{value.Key}' during map in extension '{writeOnlyValues.Item1.GetType().GetFriendlyName()}'.", exception);
                        }

                writeOnlyView.ResetCaches();
            }
        }

        // TODO Handle timeout correctly, ie. decrement remaining time in each for loop.
        public async Task OnSaveAsync(TimeSpan timeout)
        {
            try
            {
                foreach (object persistenceParticipant in persistenceParticipants)
                {
                    if (persistenceParticipant is System.Activities.Persistence.PersistenceParticipant legacyPersistenceParticipant && legacyPersistenceParticipant.IsIOParticipant())
                        await (legacyPersistenceParticipant as System.Activities.Persistence.PersistenceIOParticipant).OnSaveAsync(readWriteView, writeOnlyView, timeout);
                    else
                        await ((persistenceParticipant as IPersistenceIOParticipant)?.OnSaveAsync(readWriteView, writeOnlyView, timeout) ?? TaskConstants.Completed);
                }
            }
            catch
            {
                Abort();
                // TODO Original pipeline seems to drop this to the floor, but the reference source is insufficient.
                throw;
            }
        }

        // TODO Handle timeout correctly, ie. decrement remaining time in each for loop.
        public async Task OnSavedAsync(TimeSpan timeout)
        {
            try
            {
                // It has no legacy equivalent.
                foreach (object persistenceParticipant in persistenceParticipants)
                    await ((persistenceParticipant as IPersistenceIOParticipant)?.OnSavedAsync(timeout) ?? TaskConstants.Completed);
            }
            catch
            {
                Abort();
                // TODO Original pipeline seems to drop this to the floor, but the reference source is insufficient.
                throw;
            }
        }

        // TODO Handle timeout correctly, ie. decrement remaining time in each for loop.
        public async Task OnLoadAsync(TimeSpan timeout)
        {
            try
            {
                foreach (object persistenceParticipant in persistenceParticipants)
                {
                    if (persistenceParticipant is System.Activities.Persistence.PersistenceParticipant legacyPersistenceParticipant && legacyPersistenceParticipant.IsIOParticipant())
                        await (legacyPersistenceParticipant as System.Activities.Persistence.PersistenceIOParticipant).OnLoadAsync(readWriteView, timeout);
                    else
                        await ((persistenceParticipant as IPersistenceIOParticipant)?.OnLoadAsync(readWriteView, timeout) ?? TaskConstants.Completed);
                }
            }
            catch
            {
                Abort();
                // TODO Original pipeline seems to drop this to the floor, but the reference source is insufficient.
                throw;
            }
        }

        public void Publish()
        {
            foreach (object persistenceParticipant in persistenceParticipants)
            {
                if (persistenceParticipant is System.Activities.Persistence.PersistenceParticipant legacyPersistenceParticipant)
                    legacyPersistenceParticipant.PublishValues(readWriteView);
                else
                    (persistenceParticipant as IPersistenceParticipant)?.PublishValues(readWriteView);

            }
        }

        protected void Abort()
        {
            foreach (object persistenceParticipant in persistenceParticipants)
            {
                if (persistenceParticipant is System.Activities.Persistence.PersistenceParticipant legacyPersistenceParticipant && legacyPersistenceParticipant.IsIOParticipant())
                    (legacyPersistenceParticipant as System.Activities.Persistence.PersistenceIOParticipant).Abort();
                else
                    (persistenceParticipant as IPersistenceIOParticipant)?.Abort();
            }
        }
    }
}
