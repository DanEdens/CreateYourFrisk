﻿using System;
using System.IO;
using System.Linq;
using System.Collections;
using System.Collections.Generic;
using MoonSharp.Interpreter;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.SceneManagement;

/// <summary>
/// The class responsible for making some people lose faith in the project. In very dire need of refactoring, 
/// but hard to do until functionality can be split into battle and overworld functions.
/// 
/// As it stands this class is a messy finite state machine that takes care of controlling not only the battle,
/// but also a lot of things it shouldn't (text manager, enemy dialogue, keyboard controls etc.)
/// If you're familiar with the term cyclomatic complexity, you probably wouldn't want to hire me at this point.
/// 
/// The eventual redesign of the UI controller will try to change over as much of the functionality to Lua.
/// As we're missing some key functionality to accomplish this, refactoring has been put off for now.
/// </summary>
public class UIController : MonoBehaviour {
    public static UIController instance;
    internal TextManager textmgr;
    public bool inited = false;

    private static Sprite actB1;
    private static Sprite fightB1;
    private static Sprite itemB1;
    private static Sprite mercyB1;
    private Image actBtn;
    private Actions action = Actions.FIGHT;
    public Actions forcedaction = Actions.NONE;
    private GameObject arenaParent;
    public GameObject psContainer;
    //private GameObject canvasParent;
    internal LuaEnemyEncounter encounter;
    private Image fightBtn;
    [HideInInspector] public FightUIController fightUI;
    private Vector2 initialHealthPos = new Vector2(250, -10); // initial healthbar position for target selection
    private Image itemBtn;
    private Image mercyBtn;

    public TextManager[] monDialogues;

    // DEBUG Making running away a bit more fun. Remove this later.
    private bool musicPausedFromRunning = false;
    //private int runawayattempts = 0;

    private int selectedAction = 0;
    private int selectedEnemy = 0;
    private int selectedItem = 0;
    private int selectedMercy = 0;
    private int mecry = 0;
    public int exp = 0;
    public int gold = 0;
    //public int frameDebug = 0;
    public UIState state;
    public UIState returnstate = UIState.NONE;
    private UIState stateAfterDialogs = UIState.DEFENDING;
    private AudioSource uiAudio;
    private Vector2 upperLeft = new Vector2(65, 190); // coordinates of the upper left menu choice
    private bool encounterHasUpdate = false; //used to check if encounter has an update function for the sake of optimization
    private bool parentStateCall = true;
    private bool childStateCalled = false;
    private bool fleeSwitch = false; //Used to check if we flew away before
    private bool[] spareList;
    private bool onDeathSwitch = false;
    private UIState lastNewState = UIState.UNUSED;
    public Dictionary<int, string[]> msgs = new Dictionary<int, string[]>();
    public List<bool> psList = new List<bool>();
    public bool[] readyToNextLine = new bool[0];
    public bool needOnDeath = false;
    public bool stated = false;
    public bool battleDialogued = false;

    public enum Actions { FIGHT, ACT, ITEM, MERCY, NONE }

    public enum UIState {
        NONE, // initial state. Used to see if a modder has changed the state before the UI controller wants to.
        ACTIONSELECT, // selecting an action (FIGHT/ACT/ITEM/MERCY)
        ATTACKING, // attack window with the rhythm thing
        DEFENDING, // being attacked by enemy, waves spawn here
        ENEMYSELECT, // selecting an enemy target for FIGHT or ACT
        ACTMENU, // open up the act menu
        ITEMMENU, // open up the item menu
        MERCYMENU, // open up the mercy menu
        ENEMYDIALOGUE, // player is visible and arena is resizing, but enemy still has own dialogue
        DIALOGRESULT, // executed an action that results in dialogue that results in UIState.ENEMYDIALOG or UIState.DEFENDING
        DONE, // Finished state of battle. Currently just returns to the mod selection screen.
        SPAREIDLE, // Used for OnSpare()'s inactivity, to make it works like OnDeath(). You don't want to go in there.
        UNUSED //Used for OnDeath. Keep this state secret, please.
    }

    public delegate void Message();
    public static event Message SendToStaticInits;

    public void ActionDialogResult(TextMessage msg, UIState afterDialogState, ScriptWrapper caller = null) { ActionDialogResult(new TextMessage[] { msg }, afterDialogState, caller); }

    public void ActionDialogResult(TextMessage[] msg, UIState afterDialogState, ScriptWrapper caller = null) {
        stateAfterDialogs = afterDialogState;
        if (caller != null)
            textmgr.SetCaller(caller);
        textmgr.SetTextQueue(msg);
        SwitchState(UIState.DIALOGRESULT);
    }

    public static void EndBattle(bool fromGameOver = false) {
        LuaSpriteController spr = (LuaSpriteController)SpriteUtil.MakeIngameSprite("black", -1).UserData.Object;
        if (GameObject.Find("TopLayer"))
            spr.layer = "Top";
        spr.Scale(640, 480);
        Inventory.RemoveAddedItems();
        GlobalControls.lastTitle = false;
        PlayerCharacter.instance.MaxHPShift = 0;
        #if UNITY_STANDALONE_WIN || UNITY_EDITOR
            if (GlobalControls.crate)  Misc.WindowName = ControlPanel.instance.WinodwBsaisNmae;
            else                       Misc.WindowName = ControlPanel.instance.WindowBasisName;
        #endif
        for (int i = 0; i < LuaScriptBinder.scriptlist.Count; i++)
            LuaScriptBinder.scriptlist[i] = null;
        LuaScriptBinder.ClearBattleVar();
        GlobalControls.stopScreenShake = true;
        MusicManager.hiddenDictionary.Clear();
        if (GlobalControls.modDev) {
            List<string> toDelete = new List<string>();
            foreach (string str in NewMusicManager.audioname.Keys)
                if (str != "src")
                    toDelete.Add(str);
            foreach (string str in toDelete)
                NewMusicManager.DestroyChannel(str);
            PlayerCharacter.instance.Reset();
            SceneManager.LoadScene("ModSelect");
        } else {
            foreach (string str in NewMusicManager.audioname.Keys)
                if (str != "StaticKeptAudio")
                    NewMusicManager.Stop(str);
            SceneManager.UnloadSceneAsync("Battle");
            GlobalControls.isInFight = false;
            PlayerOverworld.ShowOverworld("Battle");
        }
    }

    public void ShowError(TextMessage msg) { ActionDialogResult(msg, UIState.ACTIONSELECT);  }

    public void SwitchState(UIState state, bool first = false) {
        stated = true;
        if (onDeathSwitch) {
            lastNewState = state;
            return;
        }
        //Pre-state
        if (fleeSwitch) {
            EndBattle();
            return;
        }
        if (parentStateCall) {
            parentStateCall = false;
            returnstate = state;
            try {
                LuaEnemyEncounter.script.Call("EnteringState", new DynValue[] { DynValue.NewString(state.ToString()), DynValue.NewString(this.state.ToString()) });
            } catch (InterpreterException ex) {
                UnitaleUtil.DisplayLuaError(LuaEnemyEncounter.script.scriptname, ex.DecoratedMessage);
            }
            parentStateCall = true;

            if (childStateCalled) {
                childStateCalled = false;
                return;
            }
        } else {
            childStateCalled = true;
        }

        // Quick and dirty addition to add some humor to the Run away command.
        if (musicPausedFromRunning) {
            Camera.main.GetComponent<AudioSource>().UnPause();
            musicPausedFromRunning = false;
        }
        // END DEBUG
        // below: actions based on ending a previous state, or actions that affect multiple states

        if (state == UIState.DEFENDING || state == UIState.ENEMYDIALOGUE) {
            PlayerController.instance.setControlOverride(state != UIState.DEFENDING);
            textmgr.DestroyText();
            PlayerController.instance.SetPositionQueue(320, 160, true);
            PlayerController.instance.GetComponent<Image>().enabled = true;
            fightBtn.overrideSprite = null;
            actBtn.overrideSprite = null;
            itemBtn.overrideSprite = null;
            mercyBtn.overrideSprite = null;
            textmgr.SetPause(true);
        } else {
            if (!first &&!ArenaManager.instance.firstTurn)
                ArenaManager.instance.resetArena();
            PlayerController.instance.invulTimer = 0.0f;
            PlayerController.instance.setControlOverride(true);
        }

        if (this.state == UIState.ENEMYSELECT && forcedaction == Actions.FIGHT)
            foreach (LifeBarController lbc in arenaParent.GetComponentsInChildren<LifeBarController>())
                Destroy(lbc.gameObject);

        if (this.state == UIState.ENEMYDIALOGUE) {
            TextManager[] textmen = FindObjectsOfType<TextManager>();
            foreach (TextManager textman in textmen)
                if (textman.gameObject.name.StartsWith("DialogBubble")) // game object name is hardcoded as it won't change
                    Destroy(textman.gameObject);
        }
        UIState oldstate = this.state;
        this.state = state;
        //encounter.CallOnSelfOrChildren("Entered" + Enum.GetName(typeof(UIState), state).Substring(0, 1)
        //                                         + Enum.GetName(typeof(UIState), state).Substring(1, Enum.GetName(typeof(UIState), state).Length - 1).ToLower());
        if (oldstate == UIState.DEFENDING && this.state != UIState.DEFENDING)
            encounter.EndWave();
        switch (this.state) {
            case UIState.ATTACKING:
                textmgr.DestroyText();
                PlayerController.instance.GetComponent<Image>().enabled = false;
                if (!fightUI.multiHit) {
                    fightUI.targetIDs = new int[] { selectedEnemy };
                    fightUI.targetNumber = 1;
                }

                fightUI.Init();
                break;

            case UIState.ACTIONSELECT:
                forcedaction = Actions.NONE;
                PlayerController.instance.setControlOverride(true);
                PlayerController.instance.GetComponent<Image>().enabled = true;
                SetPlayerOnAction(action);
                textmgr.SetPause(ArenaManager.instance.isResizeInProgress());
                textmgr.SetCaller(LuaEnemyEncounter.script); // probably not necessary due to ActionDialogResult changes
                if (encounter.EncounterText == null) {
                    encounter.EncounterText = "";
                    UnitaleUtil.WriteInLogAndDebugger("[WARN]There is no encounter text!");
                }
                textmgr.SetText(new RegularMessage(encounter.EncounterText));
                break;

            case UIState.ACTMENU:
                string[] actions = new string[encounter.EnabledEnemies[selectedEnemy].ActCommands.Length];
                for (int i = 0; i < actions.Length; i++)
                    actions[i] = encounter.EnabledEnemies[selectedEnemy].ActCommands[i];

                selectedAction = 0;
                SetPlayerOnSelection(selectedAction);
                textmgr.SetText(new SelectMessage(actions, false));
                break;

            case UIState.ITEMMENU:
                battleDialogued = false;
                string[] items = GetInventoryPage(0);
                selectedItem = 0;
                SetPlayerOnSelection(0);
                textmgr.SetText(new SelectMessage(items, false));
                /*ActionDialogResult(new TextMessage[] {
                    new TextMessage("Can't open inventory.\nClogged with pasta residue.", true, false),
                    new TextMessage("Might also be a dog.\nIt's ambiguous.",true,false)
                }, UIState.ENEMYDIALOG);*/
                break;

            case UIState.MERCYMENU:
                if (LuaScriptBinder.Get(null, "ForceNoFlee") != null) {
                    LuaEnemyEncounter.script.SetVar("flee", DynValue.NewBoolean(false));
                    LuaScriptBinder.Remove("ForceNoFlee");
                } else if (!LuaEnemyEncounter.script.GetVar("flee").Boolean && LuaEnemyEncounter.script.GetVar("flee").Type != DataType.Nil)
                    encounter.CanRun = false;
                else
                    encounter.CanRun = true;
                selectedMercy = 0;
                string[] mercyopts = new string[1 + (encounter.CanRun ? 1 : 0)];
                mercyopts[0] = "Spare";
                foreach (EnemyController enemy in encounter.EnabledEnemies)
                    if (enemy.CanSpare) {
                        mercyopts[0] = "[starcolor:ffff00][color:ffff00]" + mercyopts[0] + "[color:ffffff]";
                        break;
                    }
                if (encounter.CanRun)
                    mercyopts[1] = "Flee";
                SetPlayerOnSelection(0);
                textmgr.SetText(new SelectMessage(mercyopts, true));
                break;

            case UIState.ENEMYSELECT:
                string[] names = new string[encounter.EnabledEnemies.Length];
                string[] colorPrefixes = new string[names.Length];
                for (int i = 0; i < encounter.EnabledEnemies.Length; i++) {
                    names[i] = encounter.EnabledEnemies[i].Name;
                    if (encounter.EnabledEnemies[i].CanSpare)
                        colorPrefixes[i] = "[color:ffff00]";
                }
                if (encounter.EnabledEnemies.Length > 3) {
                    selectedEnemy = 0;
                    string[] newnames = new string[3];
                    newnames[0] = names[0];
                    newnames[1] = names[1];
                    colorPrefixes[2] = "";
                    newnames[2] = "\tPAGE 1";
                    names = newnames;
                }
                for (int i = 0; i < names.Length; i++)
                    names[i] += "[color:ffffff]";                    
                textmgr.SetText(new SelectMessage(names, true, colorPrefixes));
                if (forcedaction != Actions.FIGHT && forcedaction != Actions.ACT)
                    forcedaction = action;
                if (forcedaction == Actions.FIGHT) {
                    int maxWidth = (int)initialHealthPos.x, count = 0;
                    
                    for (int i = 0; i < encounter.EnabledEnemies.Length; i++) {
                        if (encounter.EnabledEnemies.Length > 3)
                            if (i > 1)
                                break;
                        //int mNameWidth = UnitaleUtil.fontStringWidth(textmgr.Charset, "* " + encounter.enabledEnemies[i].Name) + 50;
                        for (int j = count; j < textmgr.textQueue[textmgr.currentLine].Text.Length; j++)
                            if (textmgr.textQueue[textmgr.currentLine].Text[j] == '\n' || textmgr.textQueue[textmgr.currentLine].Text[j] == '\r')
                                break;
                        count++;
                        //int mNameWidth = (int)UnitaleUtil.calcTotalLength(textmgr, lastCount, count);
                        for (int j = 0; j <= 1 && j < encounter.EnabledEnemies.Length; j++) {
                            int mNameWidth = (int)UnitaleUtil.CalcTotalLength(textmgr) + 50;
                            if (mNameWidth > maxWidth)
                                maxWidth = mNameWidth;
                        }
                    }
                    for (int i = 0; i < encounter.EnabledEnemies.Length; i++) {
                        if (encounter.EnabledEnemies.Length > 3)
                            if (i > 1)
                                break;
                        LifeBarController lifebar = Instantiate(Resources.Load<LifeBarController>("Prefabs/HPBar"));
                        lifebar.player = true;
                        lifebar.transform.SetParent(textmgr.transform);
                        lifebar.transform.SetAsFirstSibling();
                        RectTransform lifebarRt = lifebar.GetComponent<RectTransform>();
                        lifebarRt.anchoredPosition = new Vector2(maxWidth, initialHealthPos.y - i * textmgr.Charset.LineSpacing);
                        lifebarRt.sizeDelta = new Vector2(90, lifebarRt.sizeDelta.y);
                        lifebar.setFillColor(Color.green);
                        float hpFrac = (float)Mathf.Abs(encounter.EnabledEnemies[i].HP) / (float)encounter.EnabledEnemies[i].MaxHP;
                        if (encounter.EnabledEnemies[i].HP < 0) {
                            lifebar.fill.rectTransform.offsetMin = new Vector2(-90 * hpFrac, 0);
                            lifebar.fill.rectTransform.offsetMax = new Vector2(-90, 0);
                        } else
                            lifebar.setInstant(hpFrac);
                    }
                }

                if (selectedEnemy >= encounter.EnabledEnemies.Length)
                    selectedEnemy = 0;
                SetPlayerOnSelection(selectedEnemy * 2); // single list so skip right row by multiplying x2
                break;

            case UIState.DEFENDING:
                ArenaManager.instance.Resize((int)encounter.ArenaSize.x, (int)encounter.ArenaSize.y);
                PlayerController.instance.setControlOverride(false);
                encounter.NextWave();
                // ActionDialogResult(new TextMessage("This is where you'd\rdefend yourself.\nBut the code was spaghetti.", true, false), UIState.ACTIONSELECT);
                break;

            case UIState.DIALOGRESULT:
                PlayerController.instance.GetComponent<Image>().enabled = false;
                break;

            case UIState.ENEMYDIALOGUE:
                PlayerController.instance.GetComponent<Image>().enabled = true;
                //ArenaManager.instance.Resize((int)encounter.ArenaSize.x, (int)encounter.ArenaSize.y);
                ArenaManager.instance.Resize(155, 130);
                encounter.CallOnSelfOrChildren("EnemyDialogueStarting");
                monDialogues = new TextManager[encounter.EnabledEnemies.Length];
                readyToNextLine = new bool[encounter.EnabledEnemies.Length];
                for (int i = 0; i < encounter.EnabledEnemies.Length; i++) {
                    this.msgs.Remove(i);
                    this.msgs.Add(i, encounter.EnabledEnemies[i].GetDefenseDialog());
                    string[] msgs = this.msgs[i];
                    if (msgs == null) {
                        UserDebugger.Warn("Entered ENEMYDIALOGUE, but no current/random dialogue was set for " + encounter.EnabledEnemies[i].Name);
                        SwitchState(UIState.DEFENDING);
                        break;
                    }
                    GameObject speechBub = Instantiate(SpriteFontRegistry.BUBBLE_OBJECT);
                    //RectTransform enemyRt = encounter.enabledEnemies[i].GetComponent<RectTransform>();
                    TextManager sbTextMan = speechBub.GetComponent<TextManager>();
                    monDialogues[i] = sbTextMan;
                    sbTextMan.SetCaller(encounter.EnabledEnemies[i].script);
                    Image speechBubImg = speechBub.GetComponent<Image>();
                    if (sbTextMan.CharacterCount(msgs[0]) == 0) speechBubImg.color = new Color(speechBubImg.color.r, speechBubImg.color.g, speechBubImg.color.b, 0);
                    else                                        speechBubImg.color = new Color(speechBubImg.color.r, speechBubImg.color.g, speechBubImg.color.b, 1);
                    SpriteUtil.SwapSpriteFromFile(speechBubImg, encounter.EnabledEnemies[i].DialogBubble, i);
                    Sprite speechBubSpr = speechBubImg.sprite;
                    // TODO improve position setting/remove hardcoding of position setting
                    speechBub.transform.SetParent(encounter.EnabledEnemies[i].transform);
                    speechBub.GetComponent<RectTransform>().anchoredPosition = encounter.EnabledEnemies[i].DialogBubblePosition;
                    speechBub.transform.position = new Vector3(speechBub.transform.position.x + encounter.EnabledEnemies[i].offsets[1].x,
                                                               speechBub.transform.position.y + encounter.EnabledEnemies[i].offsets[1].y, speechBub.transform.position.z);
                    sbTextMan.SetOffset(speechBubSpr.border.x, -speechBubSpr.border.w);
                    //sbTextMan.setFont(SpriteFontRegistry.Get(SpriteFontRegistry.UI_MONSTERTEXT_NAME));
                    sbTextMan.SetFont(SpriteFontRegistry.Get(encounter.EnabledEnemies[i].Font));
                    sbTextMan.SetEffect(new RotatingEffect(sbTextMan));

                    MonsterMessage[] monMsgs = new MonsterMessage[msgs.Length];
                    for (int j = 0; j < monMsgs.Length; j++)
                        monMsgs[j] = new MonsterMessage(msgs[j]);

                    sbTextMan.SetTextQueue(monMsgs);
                    speechBub.GetComponent<Image>().enabled = true;
                    if (encounter.EnabledEnemies[i].Voice != "")
                        sbTextMan.letterSound.clip = AudioClipRegistry.GetVoice(encounter.EnabledEnemies[i].Voice);
                }
                break;

            case UIState.DONE:
                //StaticInits.Reset();
                //LuaEnemyEncounter.script.SetVar("unescape", DynValue.NewBoolean(false));
                EndBattle();
                break;
        }
    }

    public static void SwitchStateOnString(Script scr, string state) {
        if (!instance.encounter.gameOverStance) {
            try {
                UIState newState = (UIState)Enum.Parse(typeof(UIState), state, true);
                instance.SwitchState(newState);
            } catch { throw new CYFException("The state " + state + " isn't a valid state."); }
        }
    }

    private void Awake() {
        if (GlobalControls.crate) {
            fightB1 = SpriteRegistry.Get("UI/Buttons/gifhtbt_1");
            actB1 = SpriteRegistry.Get("UI/Buttons/catbt_1");
            itemB1 = SpriteRegistry.Get("UI/Buttons/tembt_1");
            mercyB1 = SpriteRegistry.Get("UI/Buttons/mecrybt_1");
        } else {
            fightB1 = SpriteRegistry.Get("UI/Buttons/fightbt_1");
            actB1 = SpriteRegistry.Get("UI/Buttons/actbt_1");
            itemB1 = SpriteRegistry.Get("UI/Buttons/itembt_1");
            mercyB1 = SpriteRegistry.Get("UI/Buttons/mercybt_1");
        }

        arenaParent = GameObject.Find("arena_border_outer");
        //canvasParent = GameObject.Find("Canvas");
        uiAudio = GetComponent<AudioSource>();
        uiAudio.clip = AudioClipRegistry.GetSound("menumove");

        instance = this;
    }
    
    public void UpdateBubble() {
        for (int i = 0; i < encounter.EnabledEnemies.Length; i++) {
            try {
                if (monDialogues[i] == null) {
                    readyToNextLine[i] = true;
                    continue;
                }
                string[] msgs = this.msgs[i];
                if (monDialogues[i].currentLine >= monDialogues[i].LineCount()) {
                    readyToNextLine[i] = true;
                    continue;
                }
                GameObject speechBub = encounter.EnabledEnemies[i].transform.Find("DialogBubble(Clone)").gameObject;
                TextManager sbTextMan = speechBub.GetComponent<TextManager>();
                readyToNextLine[i] = false;
                Image speechBubImg = speechBub.GetComponent<Image>();
                if (monDialogues[i].CharacterCount(msgs[monDialogues[i].currentLine]) == 0) speechBubImg.color = new Color(speechBubImg.color.r, speechBubImg.color.g, speechBubImg.color.b, 0);
                else                                                                        speechBubImg.color = new Color(speechBubImg.color.r, speechBubImg.color.g, speechBubImg.color.b, 1);

                SpriteUtil.SwapSpriteFromFile(speechBubImg, encounter.EnabledEnemies[i].DialogBubble, i);
                Sprite speechBubSpr = speechBubImg.sprite;
                sbTextMan.SetOffset(speechBubSpr.border.x, -speechBubSpr.border.w);
                speechBub.GetComponent<RectTransform>().anchoredPosition = encounter.EnabledEnemies[i].DialogBubblePosition;
                speechBub.transform.position = new Vector3(speechBub.transform.position.x + encounter.EnabledEnemies[i].offsets[1].x,
                                                           speechBub.transform.position.y + encounter.EnabledEnemies[i].offsets[1].y, speechBub.transform.position.z);
                sbTextMan.SetFont(SpriteFontRegistry.Get(encounter.EnabledEnemies[i].Font));
                if (encounter.EnabledEnemies[i].Voice != "")
                    sbTextMan.letterSound.clip = AudioClipRegistry.GetVoice(encounter.EnabledEnemies[i].Voice);
            } catch {
                new CYFException("Error while updating the monster n°" + i);
            }
        }
    }

    private void UpdateMonsterDialogue() {
        for (int i = 0; i < monDialogues.Length; i++) {
            if (readyToNextLine[i])       continue;
            if (monDialogues[i] == null) {
                readyToNextLine[i] = true;
                continue;
            }
            if (monDialogues[i].CanAutoSkip()) {
                readyToNextLine[i] = true;
                DoNextMonsterDialogue(false, i);
            }
            if (monDialogues[i].CanAutoSkipAll()) {
                for (int j = 0; j < monDialogues.Length; j++)
                    readyToNextLine[j] = true;
                DoNextMonsterDialogue();
                return;
            }
            if ((monDialogues[i].AllLinesComplete() && monDialogues[i].LineCount() != 0) || monDialogues[i].CanAutoSkipThis() || (!monDialogues[i].AllLinesComplete() && monDialogues[i].LineComplete())) {
                readyToNextLine[i] = true;
                continue;
            }
        }
    }

    public void DoNextMonsterDialogue(bool singleLineAll = false, int index = -1) {
        bool complete = true, foiled = false;
        if (index != -1) {
            if (monDialogues[index] == null)
                return;

            if (monDialogues[index].HasNext()) {
                FileInfo fi = new FileInfo(FileLoader.pathToDefaultFile("Sprites/" + encounter.EnabledEnemies[index].DialogBubble + ".png"));
                if (!fi.Exists)
                    fi = new FileInfo(FileLoader.pathToModFile("Sprites/" + encounter.EnabledEnemies[index].DialogBubble + ".png"));
                if (!fi.Exists) {
                    Debug.LogError("The bubble " + encounter.EnabledEnemies[index].DialogBubble + ".png doesn't exist.");
                } else {
                    Sprite speechBubSpr = SpriteUtil.FromFile(fi.FullName);
                    monDialogues[index].SetOffset(speechBubSpr.border.x, -speechBubSpr.border.w);
                }
                monDialogues[index].NextLineText();
                complete = false;
            } else {
                monDialogues[index].DestroyText();
                GameObject.Destroy(monDialogues[index].gameObject);
            }
            if (complete)
                for (int i = 0; i < monDialogues.Length; i++)
                    if (monDialogues[i] != null)
                        complete = false;
        } else if (!singleLineAll)
            for (int i = 0; i < monDialogues.Length; i++) {
                if (monDialogues[i] == null)
                    continue;

                if ((monDialogues[i].AllLinesComplete() && monDialogues[i].LineCount() != 0) || (!monDialogues[i].HasNext() && readyToNextLine[i])) {
                    monDialogues[i].DestroyText();
                    GameObject.Destroy(monDialogues[i].gameObject); // this text manager's game object is a dialog bubble and should be destroyed at this point
                    continue;
                } else
                    complete = false;

                // part that autoskips text if [nextthisnow] or [finished] is introduced
                if (monDialogues[i].CanAutoSkipThis() || monDialogues[i].CanAutoSkip()) {
                    if (monDialogues[i].HasNext()) {
                        FileInfo fi = new FileInfo(FileLoader.pathToDefaultFile("Sprites/" + encounter.EnabledEnemies[i].DialogBubble + ".png"));
                        if (!fi.Exists)
                            fi = new FileInfo(FileLoader.pathToModFile("Sprites/" + encounter.EnabledEnemies[i].DialogBubble + ".png"));
                        if (!fi.Exists) {
                            Debug.LogError("The bubble " + encounter.EnabledEnemies[i].DialogBubble + ".png doesn't exist.");
                        } else {
                            Sprite speechBubSpr = SpriteUtil.FromFile(fi.FullName);
                            monDialogues[i].SetOffset(speechBubSpr.border.x, -speechBubSpr.border.w);
                        }
                        monDialogues[i].NextLineText();
                    } else {
                        monDialogues[i].DestroyText();
                        GameObject.Destroy(monDialogues[i].gameObject); // code duplication? in my source? it's more likely than you think
                        if (!foiled)
                            complete = true;
                        continue;
                    }
                } else if (readyToNextLine[i]) {
                    FileInfo fi = new FileInfo(FileLoader.pathToDefaultFile("Sprites/" + encounter.EnabledEnemies[i].DialogBubble + ".png"));
                    if (!fi.Exists)
                        fi = new FileInfo(FileLoader.pathToModFile("Sprites/" + encounter.EnabledEnemies[i].DialogBubble + ".png"));
                    if (!fi.Exists) {
                        Debug.LogError("The bubble " + encounter.EnabledEnemies[i].DialogBubble + ".png doesn't exist.");
                    } else {
                        Sprite speechBubSpr = SpriteUtil.FromFile(fi.FullName);
                        monDialogues[i].SetOffset(speechBubSpr.border.x, -speechBubSpr.border.w);
                    }
                    monDialogues[i].NextLineText();
                }
                if (!complete)
                    foiled = true;
            }
        if (!complete || foiled)
            UpdateBubble();
        // looping through the same list three times? there's a reason this class is the most in need of redoing
        // either way, after doing everything required, check which text manager has the longest text now and mute all others
        int longestTextLen = 0;
        int longestTextMgrIndex = -1;
        for (int i = 0; i < monDialogues.Length; i++) {
            if (monDialogues[i] == null)
                continue;
            monDialogues[i].SetMute(true);
            if (!monDialogues[i].AllLinesComplete() && monDialogues[i].letterReferences.Length > longestTextLen) {
                longestTextLen = monDialogues[i].letterReferences.Length - monDialogues[i].currentReferenceCharacter;
                longestTextMgrIndex = i;
            }
        }

        if (longestTextMgrIndex > -1)
            monDialogues[longestTextMgrIndex].SetMute(false);

        if (!complete) // break if we're not done with all text
            return;
        if (encounter.EnabledEnemies.Length > 0) {
            encounter.CallOnSelfOrChildren("EnemyDialogueEnding");
            SwitchState(UIState.DEFENDING);
        }
    }

    private string[] GetInventoryPage(int page) {
        int invCount = 0;
        for (int i = page * 4; i < page * 4 + 4; i++) {
            if (Inventory.inventory.Count <= i)
                break;

            invCount++;
        }

        if (invCount == 0)
            return null;

        string[] items = new string[6];
        for (int i = 0; i < invCount; i++) {
            items[i] = Inventory.inventory[i + page * 4].ShortName;
        }
        items[5] = "PAGE " + (page + 1);
        return items;
    }

    private string[] GetEnemyPage(int page, out string[] colors) {
        int enemyCount = 0;
        for (int i = page * 2; i < page * 2 + 2; i++) {
            if (encounter.EnabledEnemies.Length <= i)
                break;
            enemyCount++;
        }
        colors = new string[6];
        string[] enemies = new string[6];
        enemies[0] = encounter.EnabledEnemies[page * 2].Name;
        if (enemyCount == 2)
            enemies[2] = "* " + encounter.EnabledEnemies[page * 2 + 1].Name;
        for (int i = page * 2; i < encounter.EnabledEnemies.Length && enemyCount > 0; i++) {
            if (encounter.EnabledEnemies[i].CanSpare)
                colors[(i - page * 2) * 2] = "[color:ffff00]";
            enemyCount--;
        }
        enemies[5] = "PAGE " + (page + 1);
        return enemies;
    }

    private void RenewLifebars(int page) {
        int maxWidth = (int)initialHealthPos.x;
        foreach (LifeBarController lbc in arenaParent.GetComponentsInChildren<LifeBarController>())
            Destroy(lbc.gameObject);
        for (int i = page * 2; i <= page * 2 + 1 && i < encounter.EnabledEnemies.Length; i++) {
            int mNameWidth = (int)UnitaleUtil.CalcTotalLength(textmgr) + 50;
            if (mNameWidth > maxWidth)
                maxWidth = mNameWidth;
        }
        for (int i = page * 2; i <= page * 2 + 1 && i < encounter.EnabledEnemies.Length; i++) {
            LifeBarController lifebar = Instantiate(Resources.Load<LifeBarController>("Prefabs/HPBar"));
            lifebar.player = true;
            lifebar.transform.SetParent(textmgr.transform);
            lifebar.transform.SetAsFirstSibling();
            RectTransform lifebarRt = lifebar.GetComponent<RectTransform>();
            lifebarRt.anchoredPosition = new Vector2(maxWidth, initialHealthPos.y - (i - page * 2) * textmgr.Charset.LineSpacing);
            lifebarRt.sizeDelta = new Vector2(90, lifebarRt.sizeDelta.y);
            lifebar.setFillColor(Color.green);
            float hpFrac = (float)Mathf.Abs(encounter.EnabledEnemies[i].HP) / (float)encounter.EnabledEnemies[i].MaxHP;
            if (encounter.EnabledEnemies[i].HP < 0) {
                lifebar.fill.rectTransform.offsetMin = new Vector2(-90 * hpFrac, 0);
                lifebar.fill.rectTransform.offsetMax = new Vector2(-90, 0);
            } else
                lifebar.setInstant(hpFrac);
        }
    }

    public UIState GetState() { return state; }

    private void HandleAction() {
        if (!stated || state == UIState.ATTACKING) 
            switch (state) {
                case UIState.ATTACKING:
                    fightUI.StopAction();
                    break;

                case UIState.DIALOGRESULT:
                    if (!textmgr.LineComplete())
                        break;

                    if (!textmgr.AllLinesComplete() && textmgr.LineComplete()) {
                        textmgr.NextLineText();
                        break;
                    } else if (textmgr.AllLinesComplete() && textmgr.LineCount() != 0) {
                        textmgr.DestroyText();
                        SwitchState(stateAfterDialogs);
                    }
                    break;

                case UIState.ACTIONSELECT:
                    switch (action) {
                        case Actions.FIGHT:
                            SwitchState(UIState.ENEMYSELECT);
                            break;

                        case Actions.ACT:
                            if (GlobalControls.crate)
                                if (ControlPanel.instance.Safe) UnitaleUtil.PlaySound("MEOW", "sounds/meow" + Math.RandomRange(1, 8));
                                else UnitaleUtil.PlaySound("MEOW", "sounds/meow" + Math.RandomRange(1, 9));
                            else SwitchState(UIState.ENEMYSELECT);
                            break;

                        case Actions.ITEM:
                            if (GlobalControls.crate) {
                                string strBasis = "TEM WANT FLAKES!!!1!1", strModif = strBasis;
                                for (int i = strBasis.Length - 2; i >= 0; i--)
                                    strModif = strModif.Substring(0, i) + "[voice:tem" + Math.RandomRange(1, 7) + "]" + strModif.Substring(i, strModif.Length - i);
                                ActionDialogResult(new TextMessage(strModif, true, false), UIState.ENEMYDIALOGUE);

                            } else {
                                if (Inventory.inventory.Count == 0) {
                                    //ActionDialogResult(new TextMessage("Your Inventory is empty.", true, false), UIState.ACTIONSELECT);
                                    return;
                                }
                                SwitchState(UIState.ITEMMENU);
                            }
                            break;

                        case Actions.MERCY:
                            if (GlobalControls.crate) {
                                switch (mecry) {
                                    case 0: ActionDialogResult(new TextMessage("You know... Seeing the engine like\rthis... It makes me want to cry.", true, false), UIState.ENEMYDIALOGUE); break;
                                    case 1: ActionDialogResult(new TextMessage("All these typos...\rCrate Your Frisk is bad.\rWe must destroy it.", true, false), UIState.ENEMYDIALOGUE); break;
                                    case 2: ActionDialogResult(new TextMessage("We have two solutions here:\rdownload the engine again.", true, false), UIState.ENEMYDIALOGUE); break;
                                    case 3: ActionDialogResult(new TextMessage("And another way. Though, I'll\rneed some time to find\rhow to do that...", true, false), UIState.ENEMYDIALOGUE); break;
                                    case 4: ActionDialogResult(new TextMessage("*sniffles* I can barely stand\rthe view... That's so\rdisgusting...", true, false), UIState.ENEMYDIALOGUE); break;
                                    case 5: ActionDialogResult(new TextMessage("I feel that I'm on the way,\rkeep the good work!", true, false), UIState.ENEMYDIALOGUE); break;
                                    case 6: ActionDialogResult(new TextMessage("Here, just a bit more...", true, false), UIState.ENEMYDIALOGUE); break;
                                    case 7: ActionDialogResult(new TextMessage("...No, I don't have it.\rStupid dog!\rPlease leave me more time!", true, false), UIState.ENEMYDIALOGUE); break;
                                    case 8: ActionDialogResult(new TextMessage("I want to puke...\rEven the engine is a\rplace of shitposts and memes.", true, false), UIState.ENEMYDIALOGUE); break;
                                    case 9: ActionDialogResult(new TextMessage("Will there be one day a place\rwhere shitposts and memes\rwill not appear?", true, false), UIState.ENEMYDIALOGUE); break;
                                    case 10: ActionDialogResult(new TextMessage("I hope so... My eyes are bleeding.", true, false), UIState.ENEMYDIALOGUE); break;
                                    case 11: ActionDialogResult(new TextMessage("Hm? Oh! Look! I have it!", true, false), UIState.ENEMYDIALOGUE); break;
                                    case 12: ActionDialogResult(new TextMessage("Let me read:", true, false), UIState.ENEMYDIALOGUE); break;
                                    case 13: ActionDialogResult(new TextMessage("\"To remove the big engine\rtypo bug...\"", true, false), UIState.ENEMYDIALOGUE); break;
                                    case 14:
                                        ActionDialogResult(new RegularMessage[]{
                                            new RegularMessage("\"...erase the AlMighties.\""),
                                            new RegularMessage("Is that all? Come on, all\rthis time lost for a\rthat easy response..."),
                                            new RegularMessage("...Sorry for the waiting.\rDo whatever you want now! :D"),
                                            new RegularMessage("But please..."),
                                            new RegularMessage("For GOD's sake..."),
                                            new RegularMessage("Remove Crate Your Frisk."),
                                            new RegularMessage("Now I'll wash my eyes with\rsome bleach."),
                                            new RegularMessage("Cya!")
                                        }, UIState.ENEMYDIALOGUE);
                                        break;
                                    default: ActionDialogResult(new TextMessage("But the dev is long gone\r(and blind).", true, false), UIState.ENEMYDIALOGUE); break;
                                }
                                mecry++;
                            } else
                                SwitchState(UIState.MERCYMENU);
                            break;
                    }
                    PlaySound(AudioClipRegistry.GetSound("menuconfirm"));
                    break;

                case UIState.ENEMYSELECT:
                    switch (forcedaction) {
                        case Actions.FIGHT:
                            // encounter.enemies[selectedEnemy].HandleAttack(-1);
                            PlayerController.instance.lastEnemyChosen = selectedEnemy + 1;
                            SwitchState(UIState.ATTACKING);
                            break;

                        case Actions.ACT:
                            SwitchState(UIState.ACTMENU);
                            break;
                    }
                    PlaySound(AudioClipRegistry.GetSound("menuconfirm"));
                    break;

                case UIState.ACTMENU:
                    PlayerController.instance.lastEnemyChosen = selectedEnemy + 1;
                    textmgr.SetCaller(encounter.EnabledEnemies[selectedEnemy].script); // probably not necessary due to ActionDialogResult changes
                    encounter.EnabledEnemies[selectedEnemy].Handle(encounter.EnabledEnemies[selectedEnemy].ActCommands[selectedAction]);
                    PlaySound(AudioClipRegistry.GetSound("menuconfirm"));
                    break;

                case UIState.ITEMMENU:
                    //encounter.HandleItem(Inventory.container[selectedItem]);
                    encounter.HandleItem(selectedItem);
                    PlaySound(AudioClipRegistry.GetSound("menuconfirm"));
                    break;

                case UIState.MERCYMENU:
                    if (selectedMercy == 0) {
                        bool[] canspare = new bool[encounter.enemies.Length];
                        int count = encounter.enemies.Length;
                        for (int i = 0; i < count; i++)
                            canspare[i] = encounter.enemies[i].CanSpare;
                        LuaEnemyController[] enabledEnTemp = encounter.EnabledEnemies;
                        //bool sparedAny = false;
                        for (int i = 0; i < count; i++) {
                            if (!enabledEnTemp.Contains(encounter.enemies[i]))
                                continue;
                            if (canspare[i]) {
                                if (!encounter.enemies[i].TryCall("OnSpare"))
                                    encounter.enemies[i].DoSpare();
                                else
                                    spareList[i] = true;
                                //sparedAny = true;
                            }
                        }
                        if (encounter.EnabledEnemies.Length > 0)
                            encounter.CallOnSelfOrChildren("HandleSpare");
                        /*if (encounter.enabledEnemies.Length > 0)
                            encounter.CallOnSelfOrChildren("HandleSpare");*/

                        /*if (sparedAny) {
                            if (encounter.enabledEnemies.Length == 0) {
                                checkAndTriggerVictory();
                                break;
                            }
                        }*/

                    } else if (selectedMercy == 1)
                        StartCoroutine(ISuperFlee());
                    PlaySound(AudioClipRegistry.GetSound("menuconfirm"));
                    break;

                case UIState.ENEMYDIALOGUE:
                    bool singleLineAll = true;
                    foreach (TextManager mgr in monDialogues) {
                        if (mgr == null)
                            continue;
                        if (mgr.LineCount() > 1 || !mgr.CanSkip()) {
                            singleLineAll = false;
                            break;
                        }
                    }
                    if (singleLineAll) {
                        foreach (TextManager mgr in monDialogues)
                            mgr.DoSkipFromPlayer();
                        textmgr.nextMonsterDialogueOnce = true;
                    } else if (!ArenaManager.instance.isResizeInProgress()) {
                        bool readyToSkip = true;
                        foreach (bool b in readyToNextLine)
                            if (!b) {
                                readyToSkip = false;
                                break;
                            }
                        if (readyToSkip)
                            DoNextMonsterDialogue();
                    }
                    break;
            }
        else
            PlaySound(AudioClipRegistry.GetSound("menuconfirm"));
    }

    private void HandleArrows() {
        bool left = InputUtil.Pressed(GlobalControls.input.Left);
        bool right = InputUtil.Pressed(GlobalControls.input.Right);
        bool up = InputUtil.Pressed(GlobalControls.input.Up);
        bool down = InputUtil.Pressed(GlobalControls.input.Down);

        switch (state) {
            case UIState.ACTIONSELECT:
                if (!left &&!right)
                    break;

                fightBtn.overrideSprite = null;
                actBtn.overrideSprite = null;
                itemBtn.overrideSprite = null;
                mercyBtn.overrideSprite = null;

                int actionIndex = (int)action;

                if (left)  actionIndex--;
                if (right) actionIndex++;
                actionIndex = Math.Mod(actionIndex, 4);
                action = (Actions)actionIndex;
                SetPlayerOnAction(action);
                PlaySound(AudioClipRegistry.GetSound("menumove"));
                break;

            case UIState.ENEMYSELECT:
                bool unpair = false;
                if (encounter.EnabledEnemies.Length > 3) {
                    if (!up &&!down &&!right &&!left) break;
                    if (right) {
                        if (selectedEnemy % 2 == 1)
                            unpair = true;
                        selectedEnemy = (selectedEnemy + 2) % encounter.EnabledEnemies.Length;
                        if (encounter.EnabledEnemies.Length % 2 == 1 && selectedEnemy < 2)
                            if (unpair) selectedEnemy = 1;
                            else selectedEnemy = 0;
                    } else if (left) {
                        if (selectedEnemy % 2 == 1)
                            unpair = true;
                        selectedEnemy = (selectedEnemy - 2 + encounter.EnabledEnemies.Length) % encounter.EnabledEnemies.Length; 
                        if (encounter.EnabledEnemies.Length % 2 == 1 && selectedEnemy > encounter.EnabledEnemies.Length - 3)
                            if (unpair && encounter.EnabledEnemies.Length % 2 == 0)      selectedEnemy = encounter.EnabledEnemies.Length - 1;
                            else if (unpair && encounter.EnabledEnemies.Length % 2 == 1) selectedEnemy = encounter.EnabledEnemies.Length - 2;
                            else if (!unpair && encounter.EnabledEnemies.Length % 2 == 0) selectedEnemy = encounter.EnabledEnemies.Length - 2;
                            else if (!unpair && encounter.EnabledEnemies.Length % 2 == 1) selectedEnemy = encounter.EnabledEnemies.Length - 1;
                    } else if ((up || down) && selectedEnemy / 2 * 2 + (selectedEnemy % 2 + 1) % 2 < encounter.EnabledEnemies.Length)
                        selectedEnemy = selectedEnemy / 2 * 2 + (selectedEnemy % 2 + 1) % 2;
                    if (right || left) {
                        string[] colors = new string[6];
                        string[] textTemp = GetEnemyPage(selectedEnemy / 2, out colors);
                        textmgr.SetText(new SelectMessage(textTemp, false, colors));
                        if (forcedaction == Actions.FIGHT)
                            RenewLifebars(selectedEnemy / 2);
                    }
                    SetPlayerOnSelection(selectedEnemy % 2 * 2);
                } else {
                    if (!up &&!down) break;
                    else if (up) selectedEnemy--;
                    else if (down) selectedEnemy++;
                    selectedEnemy = (selectedEnemy + encounter.EnabledEnemies.Length) % encounter.EnabledEnemies.Length;
                    SetPlayerOnSelection(selectedEnemy * 2);
                }
                break;

            case UIState.ACTMENU:
                if (!up &&!down &&!left &&!right)
                    return;

                int xCol = selectedAction % 2; // can just use remainder here, xCol will never be negative at this part
                int yCol = selectedAction / 2;

                if (left)       xCol--;
                else if (right) xCol++;
                else if (up)    yCol--;
                else if (down)  yCol++;

                int actionCount = encounter.EnabledEnemies[selectedEnemy].ActCommands.Length;
                int leftColSize = (actionCount + 1) / 2;
                int rightColSize = actionCount / 2;

                if (left || right) xCol = Math.Mod(xCol, 2);
                if (up || down)    yCol = xCol == 0 ? Math.Mod(yCol, leftColSize) : Math.Mod(yCol, rightColSize);
                int desiredAction = yCol * 2 + xCol;
                if (desiredAction >= 0 && desiredAction < actionCount) {
                    selectedAction = desiredAction;
                    SetPlayerOnSelection(selectedAction);
                }
                break;

            case UIState.ITEMMENU:
                if (!up &&!down &&!left &&!right)
                    return;

                int xColI = Math.Mod(selectedItem, 2);
                int yColI = Math.Mod(selectedItem, 4) / 2;

                if (left)       xColI--;
                else if (right) xColI++;
                else if (up)    yColI--;
                else if (down)  yColI++;

                // UnitaleUtil.writeInLog("xCol after controls " + xColI);
                // UnitaleUtil.writeInLog("yCol after controls " + yColI);

                int itemCount = 4; // HACK: should do item count based on page number...
                int leftColSizeI = (itemCount + 1) / 2;
                int rightColSizeI = itemCount / 2;
                int desiredItem = (selectedItem / 4) * 4;
                if (xColI == -1) {
                    xColI = 1;
                    desiredItem -= 4;
                } else if (xColI == 2) {
                    xColI = 0;
                    desiredItem += 4;
                }

                if (up || down)
                    yColI = xColI == 0 ? Math.Mod(yColI, leftColSizeI) : Math.Mod(yColI, rightColSizeI);
                desiredItem += (yColI * 2 + xColI);

                // UnitaleUtil.writeInLog("xCol after evaluation " + xColI);
                // UnitaleUtil.writeInLog("yCol after evaluation " + yColI);

                // UnitaleUtil.writeInLog("Unchecked desired item " + desiredItem);

                if (desiredItem < 0)                              desiredItem = Math.Mod(desiredItem, 4) + (Inventory.inventory.Count / 4) * 4;
                else if (desiredItem > Inventory.inventory.Count) desiredItem = Math.Mod(desiredItem, 4);

                if (desiredItem != selectedItem && desiredItem < Inventory.inventory.Count) { // 0 check not needed, done before
                    selectedItem = desiredItem;
                    SetPlayerOnSelection(Math.Mod(selectedItem, 4));
                    int page = selectedItem / 4;
                    textmgr.SetText(new SelectMessage(GetInventoryPage(page), false));
                }

                // UnitaleUtil.writeInLog("Desired item index after evaluation " + desiredItem);
                break;

            case UIState.MERCYMENU:
                if (!up &&!down)     break;
                if (up)               selectedMercy--;
                if (down)             selectedMercy++;
                if (encounter.CanRun) selectedMercy = Math.Mod(selectedMercy, 2);
                else                   selectedMercy = 0;

                SetPlayerOnSelection(selectedMercy * 2);
                break;
        }
    }

    private void HandleCancel() {
        switch (state) {
            case UIState.ACTIONSELECT:
            case UIState.DIALOGRESULT:
                if (textmgr.CanSkip() &&!textmgr.LineComplete())
                    textmgr.DoSkipFromPlayer();
                    //textmgr.skipText();
                break;

            case UIState.ENEMYDIALOGUE:
                bool singleLineAll = true;
                bool cannotSkip = false;
                // why two booleans for the same result? 'cause they're different conditions
                foreach (TextManager mgr in monDialogues) {
                    if (!mgr.CanSkip())
                        cannotSkip = true;

                    if (mgr.LineCount() > 1)
                        singleLineAll = false;
                }

                if (cannotSkip || singleLineAll)
                    break;

                foreach (TextManager mgr in monDialogues)
                    mgr.DoSkipFromPlayer();
                    //mgr.skipText();
                break;

            case UIState.ACTMENU:
                SwitchState(UIState.ENEMYSELECT);
                break;

            case UIState.ENEMYSELECT:
            case UIState.ITEMMENU:
            case UIState.MERCYMENU:
                SwitchState(UIState.ACTIONSELECT);
                break;
        }
    }

    private void PlaySound(AudioClip clip) {
        if (!uiAudio.clip.Equals(clip))
            uiAudio.clip = clip;
        uiAudio.Play();
    }

    public static void PlaySoundSeparate(AudioClip clip) { UnitaleUtil.PlaySound("SeparateSound", clip, 0.95f); }

    private void SetPlayerOnAction(Actions action) {
        switch (action) {
            case Actions.FIGHT:
                fightBtn.overrideSprite = fightB1;
                PlayerController.instance.SetPosition(48, 25, true);
                break;

            case Actions.ACT:
                actBtn.overrideSprite = actB1;
                PlayerController.instance.SetPosition(202, 25, true);
                break;

            case Actions.ITEM:
                itemBtn.overrideSprite = itemB1;
                PlayerController.instance.SetPosition(361, 25, true);
                break;

            case Actions.MERCY:
                mercyBtn.overrideSprite = mercyB1;
                PlayerController.instance.SetPosition(515, 25, true);
                break;
        }
    }

    // visualisation:
    // 0    1
    // 2    3
    // 4    5
    private void SetPlayerOnSelection(int selection) {
        int xMv = selection % 2; // remainder safe again, selection is never negative
        int yMv = selection / 2;
        // HACK: remove hardcoding of this sometime, ever... probably not happening lmao
        PlayerController.instance.SetPosition(upperLeft.x + xMv * 256, upperLeft.y - yMv * textmgr.Charset.LineSpacing, true);
    }

    private void Start() {
        GameObject.Find("HideEncounter").GetComponent<Image>().sprite = Sprite.Create(GlobalControls.texBeforeEncounter, 
                                                                                      new Rect(0, 0, GlobalControls.texBeforeEncounter.width, GlobalControls.texBeforeEncounter.height), 
                                                                                      new Vector2(0.5f, 0.5f));
        //if (GameOverBehavior.gameOverContainer)
        //    GameObject.Destroy(GameOverBehavior.gameOverContainer);
        GameOverBehavior.gameOverContainer = GameObject.Find("GameOverContainer");
        GameOverBehavior.gameOverContainer.SetActive(false);
        MusicManager.src = Camera.main.GetComponent<AudioSource>();
        //NewMusicManager.OnLevelWasLoaded();

        if (NewMusicManager.audiolist.ContainsKey("src"))
            NewMusicManager.audiolist.Remove("src");
        if (NewMusicManager.audiolist.ContainsKey("StaticKeptAudio"))
            NewMusicManager.audiolist.Remove("StaticKeptAudio");

        MusicManager.src = Camera.main.GetComponent<AudioSource>();
        NewMusicManager.audiolist.Add("src", MusicManager.src);
        if (PlayerOverworld.audioKept)
            NewMusicManager.audiolist.Add("StaticKeptAudio", PlayerOverworld.audioKept);

        GlobalControls.ppcollision = false;
        ControlPanel.instance.FrameBasedMovement = false;
        textmgr = GameObject.Find("TextManager").GetComponent<TextManager>();
        textmgr.SetEffect(new TwitchEffect(textmgr));
        encounter = FindObjectOfType<LuaEnemyEncounter>();

        fightBtn = GameObject.Find("FightBt").GetComponent<Image>();
        actBtn = GameObject.Find("ActBt").GetComponent<Image>();
        itemBtn = GameObject.Find("ItemBt").GetComponent<Image>();
        mercyBtn = GameObject.Find("MercyBt").GetComponent<Image>();
        if (GlobalControls.crate) {
            fightBtn.sprite = SpriteRegistry.Get("UI/Buttons/gifhtbt_0");
            fightBtn.GetComponent<AutoloadResourcesFromRegistry>().SpritePath = "UI/Buttons/gifhtbt_0";
            actBtn.sprite = SpriteRegistry.Get("UI/Buttons/catbt_0");
            actBtn.GetComponent<AutoloadResourcesFromRegistry>().SpritePath = "UI/Buttons/catbt_0";
            itemBtn.sprite = SpriteRegistry.Get("UI/Buttons/tembt_0");
            itemBtn.GetComponent<AutoloadResourcesFromRegistry>().SpritePath = "UI/Buttons/tembt_0";
            mercyBtn.sprite = SpriteRegistry.Get("UI/Buttons/mecrybt_0");
            mercyBtn.GetComponent<AutoloadResourcesFromRegistry>().SpritePath = "UI/Buttons/mecrybt_0";
        }
        StaticInits.SendLoaded();
    }

    // Use this for initialization
    private void LateStart() {
        GameObject.Destroy(GameObject.Find("HideEncounter"));
        psContainer = new GameObject("psContainer");
        psContainer.transform.SetAsFirstSibling();

        //Play that funky music
        if (MusicManager.IsStoppedOrNull(PlayerOverworld.audioKept))
            GameObject.Find("Main Camera").GetComponent<AudioSource>().Play();

        ArenaManager.instance.ResizeImmediate(ArenaManager.UIWidth, ArenaManager.UIHeight);
        //ArenaManager.instance.MoveToImmediate(0, -160, false);

        if (SendToStaticInits != null)
            SendToStaticInits();

        PlayerController.instance.Awake();
        PlayerController.instance.setControlOverride(true);
        PlayerController.instance.SetPosition(48, 25, true);
        fightUI = GameObject.Find("FightUI").GetComponent<FightUIController>();
        fightUI.gameObject.SetActive(false);

        LuaScriptBinder.CopyToBattleVar();
        spareList = new bool[encounter.enemies.Length];
        for (int i = 0; i < spareList.Length; i ++)
            spareList[i] = false;
        if (LuaEnemyEncounter.script.GetVar("Update") != null)
            encounterHasUpdate = true;
        GameObject.Find("Main Camera").GetComponent<ProjectileHitboxRenderer>().enabled = !GameObject.Find("Main Camera").GetComponent<ProjectileHitboxRenderer>().enabled;
        //There are scene init bugs, let's fix them!
        /*if (GameObject.Find("TopLayer").transform.parent != GameObject.Find("Canvas").transform) {
            RectTransform[] rts = GameObject.Find("Canvas").GetComponentsInChildren<RectTransform>(true);
            rts[rts.Length - 1].SetParent(rts[rts.Length - 2]);
            GameObject.Find("TopLayer").transform.SetParent(GameObject.Find("Canvas").transform);
            rts[rts.Length - 2].SetAsLastSibling();
        } else {*/
        /*bool toAdd = false; int indexDeb = 0, indexText = 0, j = 0;
        Transform[] rts = UnitaleUtil.GetFirstChildren(GameObject.Find("Canvas").transform, true);
        foreach (Transform rt in rts) {
            if (rt.gameObject.name == "Text") {
                rt.SetParent(GameObject.Find("Debugger").transform);
                indexText = j;
                toAdd = true;
                break;
            } else if (rt.gameObject.name == "Debugger") {
                rt.SetAsLastSibling();
                indexDeb = j;
                break;
            }
            j++;
        }
        if (toAdd)
            rts[indexText].SetParent(rts[indexDeb]);*/
        //}
        if (GlobalControls.crate) {
            UserDebugger.instance.gameObject.transform.GetChild(0).gameObject.GetComponent<Text>().text = "DEGUBBER (F9 OT TOGLGE, DEBUG(STIRNG) TO PRNIT)";
            GameObject.Find("HPLabelCrate").GetComponent<Image>().enabled = true;
            GameObject.Find("HPLabel").GetComponent<Image>().enabled = false;
        }
        encounter.CallOnSelfOrChildren("EncounterStarting");
        if (GameObject.Find("Text")) {
            GameObject.Find("Text").transform.SetParent(UserDebugger.instance.transform);
            UserDebugger.instance.transform.SetAsLastSibling();
        }

        if (!stated)
            SwitchState(UIState.ACTIONSELECT, true);
    }

    public void CheckAndTriggerVictory() {
        if (encounter.EnabledEnemies.Length > 0)
            return;
        Camera.main.GetComponent<AudioSource>().Stop();
        bool levelup = PlayerCharacter.instance.AddBattleResults(exp, gold);
        Inventory.RemoveAddedItems();
        MusicManager.SetSoundDictionary("RESETDICTIONARY", "");
        if (levelup && exp != 0) {
            UIStats.instance.setPlayerInfo(PlayerCharacter.instance.Name, PlayerCharacter.instance.LV);
            UIStats.instance.setMaxHP();
            UIStats.instance.setHP(PlayerCharacter.instance.HP);
            ActionDialogResult(new RegularMessage("[sound:levelup]YOU WON!\nYou earned "+ exp +" XP and "+ gold +" gold.\nYour LOVE increased."), UIState.DONE);
        } else
            ActionDialogResult(new RegularMessage("YOU WON!\nYou earned " + exp + " XP and " + gold + " gold."), UIState.DONE);
    }

    public void SuperFlee() {
        if ((state == UIState.ENEMYSELECT && selectedEnemy == 0) || (state == UIState.ITEMMENU && selectedItem == 0) || (state == UIState.MERCYMENU && selectedMercy == 0))
            StartCoroutine(ISuperFlee());
        else
            GlobalControls.fleeIndex = 0;
    }

    IEnumerator ISuperFlee() {
        PlayerController.instance.GetComponent<Image>().enabled = false;
        AudioClip yay = AudioClipRegistry.GetSound("runaway");
        UnitaleUtil.PlaySound("Mercy", yay, 0.65f);
        
        string[] fleeTexts;
        DynValue tempFleeTexts = LuaEnemyEncounter.script.GetVar("fleetexts");
        if (tempFleeTexts.Type == DataType.Table) {
            fleeTexts = new string[tempFleeTexts.Table.Length];
            for (int i = 0; i < tempFleeTexts.Table.Length; i++)
                fleeTexts[i] = tempFleeTexts.Table.Get(i + 1).String;
        } else if (ControlPanel.instance.Safe)
            fleeTexts = new string[] { "I'm outta here.",  "I've got better things to do.", "Don't waste my time.",
                                       "Nah, I don't like you.", "I just wanted to walk\ra bit. Leave me alone.", "You're cute, I won't kill you :3",
                                       "Better safe than sorry.", "Do as if you've never saw\rthem and walk away.", "I'll kill you last.",
                                       "Nope. [w:5]Nope. Nope. Nope. Nope.", "Wait for me, Rhenaud!", "Flee like sissy!" };
        else
            fleeTexts = new string[] { "I'm outta here.", "I've got shit to do.", "I've got better things to do.", "Don't waste my time.", "Fuck this shit I'm out.",
                                       "Nah, I don't like you.", "I just wanted to walk\ra bit. Leave me alone.", "You're cute, I won't kill you :3",
                                       "Better safe than sorry.", "Do as if you've never saw\rthem and walk away.", "I'll kill you last.",
                                       "Nope. [w:5]Nope. Nope. Nope. Nope.", "Wait for me, Rhenaud!", "Flee like sissy!" };

        /*string[] text = { "See mom, I can flee!", "LEGZ!", "It looks more like a\nreal flee.", "/me flees", "*flees*", "To infinity and beyond!",
                            "Yeah, that's the secret.\nI hope you liked it!"};*/

        ActionDialogResult(new TextMessage[] { new RegularMessage(fleeTexts[Math.RandomRange(0, fleeTexts.Length)]) }, UIState.ENEMYDIALOGUE);
        fleeSwitch = true;

        Camera.main.GetComponent<AudioSource>().Pause();
        LuaSpriteController spr = (LuaSpriteController)SpriteUtil.MakeIngameSprite("spr_heartgtfo_0", "Top").UserData.Object;
        spr.x = PlayerController.instance.transform.position.x;
        spr.y = PlayerController.instance.transform.position.y;
        spr.SetAnimation(new string[] { "spr_heartgtfo_0", "spr_heartgtfo_1" }, 1 / 10f);
        spr.color = new float[] { PlayerController.instance.GetComponent<Image>().color.r, PlayerController.instance.GetComponent<Image>().color.g,
                                  PlayerController.instance.GetComponent<Image>().color.b };
        while (spr.x > -20) {
            spr.x--;
            yield return 0;
        }
        GlobalControls.fleeIndex = 0;
    }

    // Update is called once per frame
    private void Update() {
        //frameDebug++;
        stated = false;
        if (!inited) {
            if (!ArenaManager.instance.firstTurn) {
                inited = true;
                LateStart();
            } else 
                return;
        }
        if (encounter.gameOverStance)
            return;
        if (encounterHasUpdate)
            encounter.TryCall("Update");
        
        ParticleSystem[] pss = GameObject.FindObjectsOfType<ParticleSystem>();
        //while (pss.Length > psList.Count)
        //    psList.Add(true);
        int a = pss.Length;
        for (int i = 0; i < a; i++) {
            //if (pss[i].IsAlive() &&!psList[i])
            //    psList[i] = true;
            if (!pss[i].IsAlive() && pss[i].gameObject.name.Contains("MonsterDuster(Clone)")) {
                pss[i].gameObject.SetActive(false);
                //psList.RemoveAt(i);
                //Debug.Log(i);
                i--; a--;
            }
        }

        if (textmgr.IsPaused() &&!ArenaManager.instance.isResizeInProgress())
            textmgr.SetPause(false);

        if (state == UIState.DIALOGRESULT)
            if (textmgr.CanAutoSkipAll() || (textmgr.CanAutoSkipThis() && textmgr.LineComplete()))
                if (textmgr.HasNext())
                    textmgr.NextLineText();
                else {
                    textmgr.DestroyText();
                    SwitchState(stateAfterDialogs);
                }

        if (state == UIState.ENEMYDIALOGUE) {
            bool allSkip = true;
            foreach (TextManager mgr in monDialogues)
                if (!mgr.CanAutoSkipThis()) {
                    allSkip = false;
                    break;
                }
            if (allSkip)  DoNextMonsterDialogue();
            else          UpdateMonsterDialogue();

        }

        if (state == UIState.DEFENDING) {
            if (!encounter.WaveInProgress()) {
                if (GlobalControls.retroMode)
                foreach (LuaProjectile p in FindObjectsOfType<LuaProjectile>())
                        BulletPool.instance.Requeue(p);
                SwitchState(UIState.ACTIONSELECT);
            } else if (!encounter.gameOverStance)
                encounter.UpdateWave();
            return;
        }

        if (!fleeSwitch)
            if (InputUtil.Pressed(GlobalControls.input.Confirm)) {
                if (state == UIState.ACTIONSELECT && !ArenaManager.instance.isMoveInProgress() && !ArenaManager.instance.isResizeInProgress() || state != UIState.ACTIONSELECT)
                    HandleAction();
            } else if (InputUtil.Pressed(GlobalControls.input.Cancel)) HandleCancel();
            else HandleArrows();
        else if (InputUtil.Pressed(GlobalControls.input.Confirm))
            SwitchStateOnString(null, "DONE");

        if (state == UIState.ATTACKING || needOnDeath) {
            if (!fightUI.Finished())
                return;
            if (state != UIState.ATTACKING && state != UIState.NONE)
                SwitchState(UIState.NONE);
            bool noOnDeath = true;
            onDeathSwitch = true;
            int tempIndex = 0;
            foreach (LuaEnemyController enemycontroller in encounter.EnabledEnemies) {
                int hp = enemycontroller.HP;
                if (hp <= 0 &&!enemycontroller.Unkillable) {
                    // fightUI.disableImmediate();
                    if (!enemycontroller.TryCall("OnDeath")) {
                        noOnDeath = false;
                        enemycontroller.DoKill();

                        if (encounter.EnabledEnemies.Length > 0)
                            SwitchState(UIState.ENEMYDIALOGUE);
                        //else
                        //    checkAndTriggerVictory();
                    } 
                }
                tempIndex++;
            }
            onDeathSwitch = false;
            if (lastNewState != UIState.UNUSED) {
                needOnDeath = false;
                SwitchState(lastNewState);
                lastNewState = UIState.UNUSED;
            } else if (noOnDeath) {
                needOnDeath = false;
                SwitchState(UIState.ENEMYDIALOGUE);
            }
        }
        if (state == UIState.MERCYMENU || state == UIState.SPAREIDLE) {
            bool toSpare = false;
            for (int i = 0; i < spareList.Length; i++) {
                if (spareList[i] &&!encounter.enemies[i].spared) {
                    if (state != UIState.SPAREIDLE)
                        state = UIState.SPAREIDLE;
                    encounter.enemies[i].TryCall("OnSpare");
                    toSpare = true;
                }
            }
            if (!toSpare)
                state = UIState.MERCYMENU;
        }
        //if (state == UIState.ENEMYDIALOGUE)
        //    if ((Vector2)arenaParent.transform.position == new Vector2(320, 90))
        //        PlayerController.instance.setControlOverride(false);
    }
}