using System.Collections.Generic;
using UnityEngine;

// Gemeinsamer Bewegungs-Helper für Pod (EAISeekerPod) und Kinder (EAISeekerDetonate).
//
// TEIL 1 - WARUM ES DEN HELPER ÜBERHAUPT GIBT (Log-Befund 2026-07-23):
// Beide Tasks trieben ihre Bewegung ausschließlich über
//     if (CanNavigatePath() && !IsCalculatingPath) FindPath(...)
// an. `EntityAlive.CanNavigatePath()` ist laut IL exakt `onGround || isSwimming ||
// bInElevator || Climbing`. Solange `onGround` false ist, öffnet das Gate nie, FindPath wird
// nie aufgerufen und die Entity steht komplett still. Deshalb gibt es hier zusätzlich den
// Direktschub-Fallback, den vanilla `EAIApproachAndAttackTarget.Update` nach dem Gate ebenfalls
// hat (IL_066f ff.).
//
// TEIL 2 - WARUM DIESE DATEI 2026-07-25 NEU GESCHRIEBEN WURDE (Ursache der Kreisbewegungen):
// Die alte Fassung rief `FindPath` in JEDEM Tick auf, sobald das Gate offen war, und schob im
// Fallback in JEDEM Tick per `SetMoveTo`. Beides ist deutlich aggressiver als vanilla, und beides
// erzeugt genau das beobachtete Kreisen:
//
//  (a) FindPath-Spam. `ASPPathFinderThread.FindPath` macht
//          entityWaitQueue.Add(entityId);
//          finishedPaths[entityId] = new PathInfoSingleTarget(...);
//      also ein ÜBERSCHREIBEN. Jede fertig berechnete Route wurde sofort von der nächsten
//      Anfrage abgelöst; die Drohne fuhr nie mehr als die ersten ein, zwei Wegpunkte ab und
//      drehte dann auf den nächsten, aus einer inzwischen veralteten Position berechneten Pfad
//      ein. Vanilla drosselt das über `pathCounter` (IL_04f2 ff.):
//          pathCounter--;  wenn > 0 -> gar nicht erst pathen
//          beim Pathen: pathCounter = 6 + GetRandom(10)                       (IL_0536)
//                       + (int)min(dist - 5, 60), falls > 0                   (IL_05fd..IL_0650)
//                       + 40, falls das Ziel > 8m TIEFER liegt                (IL_054f)
//          vorzeitiges Zurücksetzen auf 0 nur bei dist < 3 und
//                       navigator.getPath().NodeCountRemaining() <= 2         (IL_04ab..IL_04eb)
//      Kernaussage: je WEITER das Ziel, desto LÄNGER hält vanilla an einer Route fest. Genau
//      das wird unten nachgebaut.
//
//  (b) SetMoveTo-Spam. `EntityMoveHelper.SetMoveTo(Vector3,bool)` setzt bei JEDEM Aufruf
//      `focusTicks = 0` und ruft `ResetStuckCheck()`, und das nullt `SideStepAngle`,
//      `moveToTicks` und `moveToFailCnt`. Bei einem Aufruf pro Tick erreichen die
//      Stuck-Zähler ihre Schwellen also nie: der MoveHelper merkt nie, dass er nicht
//      vorankommt, legt sich nie auf eine Ausweichrichtung fest und schrubbt endlos im Bogen
//      an der Geometrie entlang. Der Aufruf setzt ausserdem `expiryTicks = 10`, ein Push hält
//      also von sich aus 10 Ticks (0.5s) - er MUSS gar nicht jeden Tick erneuert werden.
//      Deshalb unten: nur neu setzen, wenn der Push abgelaufen ist oder das Ziel spürbar
//      gewandert ist.
//
// Reihenfolge bleibt: erst A* versuchen (umläuft Wände), Direktschub nur als Notnagel, damit
// nie wieder ein kompletter Stillstand entsteht.
public static class SeekerMove
{
    // Wie lange ohne Pfad gewartet wird, bevor der grobe Direktschub übernimmt. Ohne diese
    // Karenz übernähme der Schub schon in den paar Frames, die der Pathfinder zum Rechnen
    // braucht, und die Bewegung flackerte zwischen A* und Geradeaus.
    private const float fallbackDelay = 0.4f;

    // Ab welcher Zielwanderung ein laufender Direktschub neu gesetzt wird (quadriert, 1m).
    private const float pushRepeatDistSq = 1f;

    // Mindestabstand zwischen zwei FindPath-Anfragen, wenn die alte Route aufgebraucht ist.
    // 4 Ticks = 0.2s -> höchstens 5 Anfragen/s statt 20 wie in der Fassung, die das Kreisen
    // verursacht hat.
    private const int MinRequestGap = 4;

    // Maximale Distanz, bis zu der der hindernisblinde Direktschub eingesetzt wird.
    private const float maxPushDist = 6f;

    private class State
    {
        public int pathCounter;             // Ticks bis zum nächsten erlaubten FindPath
        public float noPathSince = -1f;     // seit wann der Navigator ohne Pfad ist
        public Vector3 pushGoal;            // Ziel des zuletzt gesetzten Direktschubs
        public bool pushing;

        // DIAGNOSE (offene Frage nach Log `..._01-09-50`): dort stand in 52 von 53 Pod-Zeilen
        // `hasPath=False`, auch bei `onGround=True canNav=True`. `PathNavigate.HasPath()` ist
        // laut IL `currentPath != null && !currentPath.isFinished()`, das heisst also wirklich
        // "keine offene Route" - die Drohnen fuhren fast durchgehend auf dem groben Direktschub,
        // und GENAU der schiebt sie durch Wände. Ungeklärt ist, ob die Anfragen gar keinen Pfad
        // liefern oder ob der Pfad sofort als "finished" gilt.
        // Diese zwei Zähler beantworten das in einem Testlauf:
        //   requests hoch + arrivals 0  -> der Pathfinder liefert für diese Entity nichts
        //   requests ~ arrivals         -> Pfade kommen an, sind aber sofort aufgebraucht
        public int pathRequests;
        public int pathArrivals;
        public bool sawPath;
        public int sinceRequest;            // Ticks seit der letzten FindPath-Anfrage
    }

    public static string GetPathStats(int entityId)
    {
        return states.TryGetValue(entityId, out State s)
            ? $"pathReq={s.pathRequests} pathArrived={s.pathArrivals}"
            : "pathReq=- pathArrived=-";
    }

    private static readonly Dictionary<int, State> states = new Dictionary<int, State>();
    private static readonly System.Random rnd = new System.Random();

    // Aufgeräumt aus Patch_SeekerCleanup (Postfix auf Entity.OnEntityUnload). Ohne das wächst
    // das Dictionary über die Sitzung, und - schlimmer - eine wiederverwendete entityId erbt
    // den Zustand ihres Vorgängers (falscher pathCounter, hängender Push).
    public static void Forget(int entityId)
    {
        states.Remove(entityId);
    }

    // Fährt `e` Richtung `targetPos`. Gibt ein kurzes Kürzel zurück, was tatsächlich passiert
    // ist (für die [Seeker*Dbg]-Logs).
    public static string DriveTo(EntityAlive e, Vector3 targetPos, EAIBase behavior)
    {
        if (e == null || e.moveHelper == null) return "noEntity";

        int id = e.entityId;
        if (!states.TryGetValue(id, out State st))
        {
            st = new State();
            states[id] = st;
        }

        GamePath.PathNavigate nav = e.getNavigator();
        bool calculating = GamePath.PathFinderThread.Instance.IsCalculatingPath(id);
        float dist = Vector3.Distance(targetPos, e.position);

        // Vorzeitiger Re-Path, wie vanilla: nur im Nahbereich und nur, wenn die aktuelle Route
        // fast aufgebraucht ist. Verhindert, dass die Drohne am letzten Wegpunkt stehen bleibt,
        // ohne die Drosselung auf Distanz auszuhebeln.
        if (dist < 3f && !calculating && nav != null)
        {
            GamePath.PathEntity p = nav.getPath();
            if (p != null && p.NodeCountRemaining() <= 2) st.pathCounter = 0;
        }

        st.pathCounter--;
        st.sinceRequest++;

        // NACHBESSERUNG 2026-07-25 (Log `..._01-27-47`): die Zähler zeigten `pathReq=1
        // pathArrived=1` bzw. `pathReq=2 pathArrived=1` bei gleichzeitig `hasPath=False` in 48
        // von 50 Stichproben. Pfade kommen also durchaus an - sie sind nur SOFORT aufgebraucht
        // (`HasPath()` = `currentPath != null && !currentPath.isFinished()`). Danach blockierte
        // der pathCounter aber noch 6-15+ Ticks jede neue Anfrage, und in genau diesem Loch
        // übernahm nach 0.4s der grobe Direktschub - im Log die häufigste Aktion überhaupt
        // (`pathHold+SetMoveTo`, 20x). Der Direktschub ist die Quelle des Wand-Clippings, also
        // ist dieses Loch das eigentliche Restproblem.
        // Vanilla fällt das nicht auf die Füsse, weil sein Fallback nur unter 2.1m greift.
        // Lösung: eine AUFGEBRAUCHTE Route darf sofort neu geplant werden - aber nie öfter als
        // alle MinRequestGap Ticks. Solange eine Route LEBT, hält der pathCounter weiterhin
        // dagegen, das Spam-Problem von vorgestern kommt also nicht zurück.
        bool livePathNow = nav != null && nav.HasPath();
        bool exhausted = !livePathNow && st.sinceRequest >= MinRequestGap;

        string how;
        if (st.pathCounter > 0 && !exhausted)
        {
            // Wir halten bewusst an der laufenden Route fest - das ist der eigentliche Fix.
            how = "pathHold";
        }
        else if (!e.CanNavigatePath())
        {
            how = "noNav";
        }
        else if (calculating)
        {
            how = "pathPending";
        }
        else
        {
            st.pathCounter = 6 + rnd.Next(10);
            float extra = Mathf.Min(dist - 5f, 60f);
            if (extra > 0f) st.pathCounter += (int)extra;
            if (targetPos.y - e.position.y < -8f) st.pathCounter += 40;

            // canBreakBlocks = false: Seeker graben nie durch die Basis. (Vanilla übergibt hier
            // true, weil Zombies das dürfen sollen.)
            e.FindPath(targetPos, e.GetMoveSpeedAggro(), false, behavior);
            st.pathRequests++;
            st.sinceRequest = 0;
            how = "FindPath";
        }

        // Solange der Navigator eine Route hat oder gerade eine plant, fasst der Fallback nichts
        // an - der Navigator schreibt dann selbst über die PathEntity-Überladung von SetMoveTo
        // in den MoveHelper, und ein zweiter Schreiber würde ihm ins Steuer greifen.
        bool hasPath = nav != null && !nav.noPathAndNotPlanningOne();

        // Flanke "es gibt jetzt eine echte, offene Route" zählen (HasPath, nicht das weichere
        // noPathAndNotPlanningOne - letzteres ist auch während der Planung true).
        bool livePath = nav != null && nav.HasPath();
        if (livePath && !st.sawPath) st.pathArrivals++;
        st.sawPath = livePath;
        if (livePath) st.pushing = false;

        if (hasPath)
        {
            st.noPathSince = -1f;
            st.pushing = false;
            return how;
        }

        if (st.noPathSince < 0f) st.noPathSince = Time.time;
        if (Time.time - st.noPathSince < fallbackDelay) return how + "+waitPath";

        // DISTANZGRENZE FÜR DEN DIREKTSCHUB (2026-07-25).
        // Der Schub ist eine GERADE ohne jede Hindernisprüfung - er ist die eigentliche Quelle
        // des Wand-Tunnelns (die Drohnen sind mit worldR 0.11 klein genug, um an dünner
        // Geometrie vorbeizurutschen). Vanilla setzt ihn deshalb nur unter 2.1m ein, also
        // ausschliesslich für die letzte Anfahrt, wo eine Gerade ohnehin frei ist.
        // Wir hatten das Limit weggelassen, weil `CanNavigatePath()` damals dauerhaft false war
        // und die Drohnen sonst komplett standen. Dieser Grund ist entfallen: Grounding
        // funktioniert, und mit dem Continue()-Fix bleiben Routen jetzt am Leben.
        // 6m statt vanillas 2.1m als Kompromiss - grosszügig genug, um kurze Pathing-Löcher zu
        // überbrücken, zu kurz, um quer durch ein Gebäude zu schieben. Darüber hinaus lieber
        // stehen bleiben: ein Ziel, zu dem kein Pfad existiert, wird ohnehin von der
        // Unerreichbar-Heuristik verworfen, und Stillstand ist sichtbar besser als eine Drohne,
        // die durch die Wand spaziert.
        if (dist > maxPushDist) return how + "+noPushFar";

        // Direktschub nur erneuern, wenn er abgelaufen ist (expiryTicks = 10 -> IsActive wird
        // false) oder das Ziel merklich gewandert ist. Ein Aufruf pro Tick würde die
        // Stuck-Erkennung des MoveHelpers dauerhaft zurücksetzen (siehe Kopfkommentar (b)).
        bool reissue = !st.pushing
                    || !e.moveHelper.IsActive
                    || (targetPos - st.pushGoal).sqrMagnitude > pushRepeatDistSq;
        if (!reissue) return how + "+push";

        e.moveHelper.SetMoveTo(targetPos, false);
        st.pushGoal = targetPos;
        st.pushing = true;
        return how + "+SetMoveTo";
    }
}
