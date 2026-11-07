using System;
using System.Collections.Generic;
using System.Text;
using EPDM.Interop.epdm;

namespace DWGToBundleTaskAddIn
{
    // Выполняет именованный переход рабочего процесса над исходным DWG/DXF сразу после
    // подтверждения публикации PDF (заменяет ненадёжный автопереход по переменной pdf).
    // В этом interop нет «сделать переход по имени»: делаем ChangeState(ref stateId, ...)
    // на целевое состояние перехода, а сам переход ищем среди реально доступных из текущего
    // состояния (GetTransition(name) чувствителен к регистру/пробелам и недоступным переходам).
    internal static class SourceWorkflowTransition
    {
        // Возвращает сообщение для журнала. Бросает только при реальной ошибке PDM; отсутствие
        // доступного перехода (карточка уже переведена) — штатный «пропуск», не ошибка задачи.
        internal static string Perform(IEdmVault11 vault, int sourceId, int folderId, string transitionName, string comment)
        {
            if (string.IsNullOrWhiteSpace(transitionName))
                throw new ArgumentException("Имя перехода не задано.", "transitionName");
            string name = transitionName.Trim();

            var file = vault.GetObject(EdmObjectType.EdmObject_File, sourceId) as IEdmFile5;
            if (file == null) throw new InvalidOperationException("Исходный DWG/DXF не найден в PDM.");
            file.Refresh();

            IEdmState5 state = file.CurrentState;
            if (state == null)
                throw new InvalidOperationException("У исходного файла нет текущего состояния (рабочий процесс не настроен).");

            string stateName = state.Name ?? "";
            List<IEdmTransition5> available = Collect(state);
            string dump = Describe(available);

            IEdmTransition5 transition = Match(available, name);
            if (transition == null)
                return "пропуск: переход «" + name + "» недоступен из состояния «" + stateName +
                       "» (уже выполнен или другое написание имени). Доступные переходы: " + dump + ".";

            if (!transition.CheckPermission())
                return "пропуск: нет прав на переход «" + (transition.Name ?? name) + "» из состояния «" + stateName +
                       "» (" + stateName + " -> " + TargetName(transition) + "). Доступные переходы: " + dump + ".";

            string from = stateName;
            string to = TargetName(transition);
            object toStateId = transition.ToStateID;
            file.ChangeState(ref toStateId, folderId, comment ?? string.Empty, 0, (int)EdmStateFlags.EdmState_Simple);

            file.Refresh();
            string now = file.CurrentState != null ? file.CurrentState.Name : "?";
            string note = string.Equals(now, to, StringComparison.OrdinalIgnoreCase) ? "" : " (ожидалось «" + to + "»)";
            return "выполнен переход «" + (transition.Name ?? name) + "»: " + from + " -> " + now + note +
                   " [было доступно: " + dump + "]";
        }

        // Все переходы из состояния: обычные (вперёд) и выходные (назад), дубли по ID убираем.
        private static List<IEdmTransition5> Collect(IEdmState5 state)
        {
            var result = new List<IEdmTransition5>();
            var seen = new HashSet<int>();
            foreach (bool exit in new[] { false, true })
            {
                try
                {
                    IEdmPos5 pos = state.GetFirstTransitionPosition(exit);
                    while (pos != null && !pos.IsNull)
                    {
                        var t = state.GetNextTransition(pos) as IEdmTransition5;
                        if (t == null) break;
                        if (seen.Add(t.ID)) result.Add(t);
                    }
                }
                catch { }
            }
            return result;
        }

        private static IEdmTransition5 Match(List<IEdmTransition5> transitions, string name)
        {
            foreach (var t in transitions)
                if (string.Equals((t.Name ?? "").Trim(), name, StringComparison.OrdinalIgnoreCase)) return t;
            string norm = Normalize(name);
            foreach (var t in transitions)
                if (string.Equals(Normalize(t.Name), norm, StringComparison.OrdinalIgnoreCase)) return t;
            return null;
        }

        private static string Normalize(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            var sb = new StringBuilder(s.Length);
            foreach (char c in s) if (!char.IsWhiteSpace(c)) sb.Append(char.ToLowerInvariant(c));
            return sb.ToString();
        }

        private static string TargetName(IEdmTransition5 t)
        {
            try { return t.ToState != null ? (t.ToState.Name ?? t.ToStateID.ToString()) : t.ToStateID.ToString(); }
            catch { return t.ToStateID.ToString(); }
        }

        private static string Describe(List<IEdmTransition5> transitions)
        {
            if (transitions.Count == 0) return "(нет доступных переходов)";
            var sb = new StringBuilder();
            for (int i = 0; i < transitions.Count; i++)
            {
                var t = transitions[i];
                if (i > 0) sb.Append("; ");
                sb.Append("«").Append(t.Name ?? "(без имени)").Append("» -> ").Append(TargetName(t));
                try { if (!t.CheckPermission()) sb.Append(" [нет прав]"); } catch { }
            }
            return sb.ToString();
        }
    }
}
