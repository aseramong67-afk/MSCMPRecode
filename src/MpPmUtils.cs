using System;
using System.Collections.Generic;
using HutongGames.PlayMaker;
using UnityEngine;

namespace MSCMPRecode
{
    // ==== Хук-вставка в PlayMaker-FSM (по мотивам BeerMP PlayMakerUtilities) ====
    public class PmHook : FsmStateAction
    {
        private Action action;
        private bool everyFrame;

        public PmHook(Action action, bool everyFrame = false)
        {
            this.action = action;
            this.everyFrame = everyFrame;
        }

        public override void OnEnter()
        {
            if (action != null) action();
            if (!everyFrame) Finish();
        }

        public override void OnUpdate()
        {
            if (everyFrame && action != null) action();
        }
    }

    public static class PmExt
    {
        public static PlayMakerFSM GetPm(this GameObject go, string fsmName)
        {
            PlayMakerFSM[] list = go.GetComponentsInChildren<PlayMakerFSM>(true);
            for (int i = 0; i < list.Length; i++)
                if (list[i].FsmName == fsmName && list[i].transform == go.transform)
                    return list[i];
            return null;
        }

        public static FsmState GetPmState(this PlayMakerFSM pm, string stateName)
        {
            FsmState[] states = pm.FsmStates;
            for (int i = 0; i < states.Length; i++)
                if (states[i].Name == stateName)
                    return states[i];
            return null;
        }

        public static bool HasState(this PlayMakerFSM pm, string stateName)
        {
            return pm.GetPmState(stateName) != null;
        }

        public static FsmEvent AddPmEvent(this PlayMakerFSM fsm, string eventName)
        {
            fsm.Fsm.InitData();
            FsmEvent fsmEvent = FsmEvent.GetFsmEvent(eventName);
            if (fsmEvent == null) fsmEvent = new FsmEvent(eventName);
            List<FsmEvent> list = new List<FsmEvent>(fsm.FsmEvents);
            list.Add(fsmEvent);
            fsm.Fsm.Events = list.ToArray();
            fsm.Fsm.InitData();
            return fsmEvent;
        }

        public static void AddGlobalTransitionPm(this PlayMakerFSM pm, FsmEvent fsmEvent, string stateName)
        {
            pm.Fsm.InitData();
            List<FsmTransition> list = new List<FsmTransition>(pm.Fsm.GlobalTransitions);
            FsmTransition t = new FsmTransition();
            t.FsmEvent = fsmEvent;
            t.ToState = stateName;
            list.Add(t);
            pm.Fsm.GlobalTransitions = list.ToArray();
            pm.Fsm.InitData();
        }

        public static void InsertPmAction(this PlayMakerFSM pm, string stateName, FsmStateAction action, int index)
        {
            FsmState state = pm.GetPmState(stateName);
            if (state == null)
            {
                ModLog.Warn("FSM '" + pm.FsmName + "' не найден стейт '" + stateName + "' на объекте " + pm.gameObject.name);
                return;
            }
            List<FsmStateAction> list = new List<FsmStateAction>(state.Actions);
            if (index >= 0) list.Insert(index, action); else list.Add(action);
            state.Actions = list.ToArray();
        }

        public static void InsertPmAction(this PlayMakerFSM pm, string stateName, Action action, int index)
        {
            pm.InsertPmAction(stateName, new PmHook(action), index);
        }

        // Замена найденного действия стейта (по типу) на хук
        public static void ReplacePmAction(this PlayMakerFSM pm, string stateName, FsmStateAction oldAction, FsmStateAction newAction)
        {
            FsmState state = pm.GetPmState(stateName);
            if (state == null) return;
            List<FsmStateAction> list = new List<FsmStateAction>(state.Actions);
            for (int i = 0; i < list.Count; i++)
            {
                if (object.ReferenceEquals(list[i], oldAction))
                {
                    list[i] = newAction;
                    break;
                }
            }
            state.Actions = list.ToArray();
        }
    }
}
