using System.Linq;
using Content.Server.Power.EntitySystems;
using Content.Shared._Sol.Medical.Virology;
using Content.Shared._Sol.Medical.Virology.Components;
using Content.Shared._Sol.Medical.Virology.Events;
using Content.Shared.Chemistry.EntitySystems;
using Content.Shared.DoAfter;
using Content.Shared.Examine;
using Content.Shared.FixedPoint;
using Content.Shared.Interaction;
using Content.Shared.Popups;
using Content.Shared.Power;
using Content.Shared.Verbs;
using Robust.Shared.Prototypes;
using Robust.Shared.Timing;

namespace Content.Server._Sol.Medical.Virology;

/// <summary>
/// Analyzer / incubator / synthesizer cycles for the bioterror clandestine lab.
/// </summary>
public sealed class ClandestineLabSystem : EntitySystem
{
    [Dependency] private readonly SharedPopupSystem _popup = default!;
    [Dependency] private readonly SharedSolutionContainerSystem _solutions = default!;
    [Dependency] private readonly PowerReceiverSystem _power = default!;
    [Dependency] private readonly PathogenStrainRegistrySystem _registry = default!;
    [Dependency] private readonly PathogenSystem _pathogen = default!;
    [Dependency] private readonly IGameTiming _timing = default!;
    [Dependency] private readonly MetaDataSystem _meta = default!;
    [Dependency] private readonly SharedDoAfterSystem _doAfter = default!;
    [Dependency] private readonly SharedAppearanceSystem _appearance = default!;

    public override void Initialize()
    {
        base.Initialize();
        SubscribeLocalEvent<ClandestineSampleAnalyzerComponent, AfterInteractUsingEvent>(OnAnalyzerInsert);
        SubscribeLocalEvent<ClandestineSampleAnalyzerComponent, SampleAnalysisDoAfterEvent>(OnAnalyzerDoAfter);
        SubscribeLocalEvent<ClandestineSampleAnalyzerComponent, PowerChangedEvent>(OnAnalyzerPowerChanged);
        SubscribeLocalEvent<ClandestineSampleAnalyzerComponent, ComponentStartup>(OnAnalyzerStartup);
        SubscribeLocalEvent<ClandestineSampleAnalyzerComponent, ExaminedEvent>(OnAnalyzerExamined);

        SubscribeLocalEvent<ClandestineCultureIncubatorComponent, AfterInteractUsingEvent>(OnIncubatorInsert);
        SubscribeLocalEvent<ClandestineCultureIncubatorComponent, ActivateInWorldEvent>(OnIncubatorActivate);
        SubscribeLocalEvent<ClandestineCultureIncubatorComponent, PowerChangedEvent>(OnIncubatorPowerChanged);
        SubscribeLocalEvent<ClandestineCultureIncubatorComponent, ComponentStartup>(OnIncubatorStartup);
        SubscribeLocalEvent<ClandestineCultureIncubatorComponent, ExaminedEvent>(OnIncubatorExamined);

        SubscribeLocalEvent<ClandestinePathogenSynthesizerComponent, AfterInteractUsingEvent>(OnSynthesizerInsert);
        SubscribeLocalEvent<ClandestinePathogenSynthesizerComponent, GetVerbsEvent<AlternativeVerb>>(OnSynthesizerVerbs);
        SubscribeLocalEvent<ClandestinePathogenSynthesizerComponent, PowerChangedEvent>(OnSynthesizerPowerChanged);
        SubscribeLocalEvent<ClandestinePathogenSynthesizerComponent, ComponentStartup>(OnSynthesizerStartup);
        SubscribeLocalEvent<ClandestinePathogenSynthesizerComponent, ExaminedEvent>(OnSynthesizerExamined);
    }

    public override void Update(float frameTime)
    {
        base.Update(frameTime);

        var incubators = EntityQueryEnumerator<ClandestineCultureIncubatorComponent>();
        while (incubators.MoveNext(out var uid, out var incubator))
        {
            TickIncubator((uid, incubator));
        }

        var synthesizers = EntityQueryEnumerator<ClandestinePathogenSynthesizerComponent>();
        while (synthesizers.MoveNext(out var uid, out var synth))
        {
            TickSynthesizer((uid, synth));
        }
    }

    private void OnAnalyzerStartup(Entity<ClandestineSampleAnalyzerComponent> machine, ref ComponentStartup args)
    {
        UpdateAnalyzerVisuals(machine);
    }

    private void OnAnalyzerPowerChanged(Entity<ClandestineSampleAnalyzerComponent> machine, ref PowerChangedEvent args)
    {
        UpdateAnalyzerVisuals(machine);
    }

    private void OnIncubatorStartup(Entity<ClandestineCultureIncubatorComponent> machine, ref ComponentStartup args)
    {
        UpdateIncubatorVisuals(machine);
    }

    private void OnIncubatorPowerChanged(Entity<ClandestineCultureIncubatorComponent> machine, ref PowerChangedEvent args)
    {
        UpdateIncubatorVisuals(machine);
    }

    private void OnSynthesizerStartup(Entity<ClandestinePathogenSynthesizerComponent> machine, ref ComponentStartup args)
    {
        UpdateSynthesizerVisuals(machine);
    }

    private void OnSynthesizerPowerChanged(Entity<ClandestinePathogenSynthesizerComponent> machine, ref PowerChangedEvent args)
    {
        UpdateSynthesizerVisuals(machine);
    }

    private void OnAnalyzerInsert(Entity<ClandestineSampleAnalyzerComponent> machine, ref AfterInteractUsingEvent args)
    {
        if (!args.CanReach || args.Handled)
            return;

        if (!TryComp<MicrobialSampleComponent>(args.Used, out _))
            return;

        if (machine.Comp.Processing)
        {
            _popup.PopupEntity(Loc.GetString("sol-bioterror-analyzer-busy"), machine, args.User);
            return;
        }

        if (!_power.IsPowered(machine.Owner))
        {
            _popup.PopupEntity(Loc.GetString("sol-bioterror-lab-unpowered"), machine, args.User);
            return;
        }

        var doAfter = new DoAfterArgs(
            EntityManager,
            args.User,
            machine.Comp.AnalysisDelay,
            new SampleAnalysisDoAfterEvent(),
            machine,
            target: args.Used,
            used: args.Used)
        {
            NeedHand = true,
            BreakOnMove = true,
            BreakOnDamage = true,
            BreakOnHandChange = true,
            BreakOnDropItem = true,
        };

        if (!_doAfter.TryStartDoAfter(doAfter))
            return;

        machine.Comp.Processing = true;
        Dirty(machine);
        UpdateAnalyzerVisuals(machine);
        args.Handled = true;
        _popup.PopupEntity(Loc.GetString("sol-bioterror-analyzer-started"), machine, args.User);
        MarkMachineDeployed(machine.Owner, analyzer: true);
    }

    private void OnAnalyzerDoAfter(Entity<ClandestineSampleAnalyzerComponent> machine, ref SampleAnalysisDoAfterEvent args)
    {
        machine.Comp.Processing = false;
        Dirty(machine);
        UpdateAnalyzerVisuals(machine);

        if (args.Cancelled || args.Handled || args.Target == null)
            return;

        if (!TryComp<MicrobialSampleComponent>(args.Target.Value, out var sample))
            return;

        sample.Analyzed = true;
        Dirty(args.Target.Value, sample);

        var traits = sample.Traits.Count == 0
            ? Loc.GetString("sol-bioterror-analyzer-no-traits")
            : string.Join(", ", sample.Traits.Select(t => t.Id));
        _popup.PopupEntity(Loc.GetString("sol-bioterror-analyzer-result",
            ("chassis", sample.ChassisId?.Id ?? "none"),
            ("quality", sample.Quality.ToString("F2")),
            ("contaminated", sample.Contaminated),
            ("traits", traits)), machine, args.User);

        args.Handled = true;
    }

    private void OnIncubatorInsert(Entity<ClandestineCultureIncubatorComponent> machine, ref AfterInteractUsingEvent args)
    {
        if (!args.CanReach || args.Handled)
            return;

        if (!TryComp<MicrobialSampleComponent>(args.Used, out var sample) || !sample.Analyzed)
        {
            _popup.PopupEntity(Loc.GetString("sol-bioterror-incubator-need-analyzed"), machine, args.User);
            return;
        }

        if (machine.Comp.CycleInProgress || machine.Comp.HasFinishedCulture)
        {
            _popup.PopupEntity(Loc.GetString("sol-bioterror-incubator-busy"), machine, args.User);
            return;
        }

        if (!_power.IsPowered(machine.Owner))
        {
            _popup.PopupEntity(Loc.GetString("sol-bioterror-lab-unpowered"), machine, args.User);
            return;
        }

        if (!TryConsumeReagent(machine.Owner, machine.Comp.NutrientReagent, machine.Comp.NutrientNeeded))
        {
            _popup.PopupEntity(Loc.GetString("sol-bioterror-incubator-need-nutrient"), machine, args.User);
            return;
        }

        args.Handled = true;
        machine.Comp.CycleInProgress = true;
        var delay = machine.Comp.CultureDelay * (2f - Math.Clamp(sample.Quality, 0.2f, 1f));
        if (sample.Contaminated)
            delay *= 1.5f;
        machine.Comp.CycleEndsAt = _timing.CurTime + delay;
        Dirty(machine);
        UpdateIncubatorVisuals(machine);

        var pending = EnsureComp<PendingCultureDataComponent>(machine);
        pending.ChassisId = sample.ChassisId;
        pending.Traits = new List<ProtoId<PathogenTraitPrototype>>(sample.Traits);
        pending.Quality = sample.Quality;
        pending.Contaminated = sample.Contaminated;
        Dirty(machine.Owner, pending);

        QueueDel(args.Used);
        _popup.PopupEntity(Loc.GetString("sol-bioterror-incubator-started"), machine, args.User);
        MarkMachineDeployed(machine.Owner, incubator: true);
    }

    private void OnIncubatorActivate(Entity<ClandestineCultureIncubatorComponent> machine, ref ActivateInWorldEvent args)
    {
        if (args.Handled || !args.Complex)
            return;

        if (!machine.Comp.HasFinishedCulture)
            return;

        if (!TryComp<PendingCultureDataComponent>(machine.Owner, out var pending))
        {
            machine.Comp.HasFinishedCulture = false;
            Dirty(machine);
            UpdateIncubatorVisuals(machine);
            return;
        }

        args.Handled = true;
        SpawnFinishedCulture(machine, pending);
        RemComp<PendingCultureDataComponent>(machine.Owner);
        machine.Comp.HasFinishedCulture = false;
        Dirty(machine);
        UpdateIncubatorVisuals(machine);
        _popup.PopupEntity(Loc.GetString("sol-bioterror-incubator-retrieved"), machine, args.User);
    }

    private void OnSynthesizerInsert(Entity<ClandestinePathogenSynthesizerComponent> machine, ref AfterInteractUsingEvent args)
    {
        if (!args.CanReach || args.Handled)
            return;

        if (machine.Comp.CycleInProgress)
        {
            _popup.PopupEntity(Loc.GetString("sol-bioterror-synth-busy"), machine, args.User);
            return;
        }

        // Empty hand activation via using a culture: load chassis/traits, or start with stabilizer beaker check.
        if (TryComp<PathogenCultureComponent>(args.Used, out var culture))
        {
            args.Handled = true;
            if (culture.IsChassisCulture && culture.ChassisId != null)
            {
                machine.Comp.PendingChassis = culture.ChassisId;
                machine.Comp.PendingViability = culture.Viability;
                _popup.PopupEntity(Loc.GetString("sol-bioterror-synth-chassis-loaded", ("chassis", culture.ChassisId.Value.Id)), machine, args.User);
            }
            else
            {
                foreach (var trait in culture.Traits)
                {
                    if (!machine.Comp.PendingTraits.Contains(trait))
                        machine.Comp.PendingTraits.Add(trait);
                }

                _popup.PopupEntity(Loc.GetString("sol-bioterror-synth-traits-loaded"), machine, args.User);
            }

            QueueDel(args.Used);
            Dirty(machine);
            return;
        }

        _popup.PopupEntity(Loc.GetString("sol-bioterror-synth-need-culture"), machine, args.User);
    }

    private void OnSynthesizerVerbs(Entity<ClandestinePathogenSynthesizerComponent> machine, ref GetVerbsEvent<AlternativeVerb> args)
    {
        if (!args.CanInteract || machine.Comp.CycleInProgress || machine.Comp.PendingChassis == null)
            return;

        var user = args.User;
        args.Verbs.Add(new AlternativeVerb
        {
            Text = Loc.GetString("sol-bioterror-synth-begin-verb"),
            Act = () => BeginSynthesis(machine, user),
            Priority = 10,
        });
    }

    private void BeginSynthesis(Entity<ClandestinePathogenSynthesizerComponent> machine, EntityUid user)
    {
        if (machine.Comp.CycleInProgress || machine.Comp.PendingChassis == null)
            return;

        if (!_power.IsPowered(machine.Owner))
        {
            _popup.PopupEntity(Loc.GetString("sol-bioterror-lab-unpowered"), machine, user);
            return;
        }

        if (!_registry.TryValidateTraits(machine.Comp.PendingTraits, machine.Comp.MaxTraitBudget, out var error))
        {
            _popup.PopupEntity(Loc.GetString("sol-bioterror-synth-invalid", ("error", error ?? "unknown")), machine, user);
            TriggerAccident(machine.Owner, machine.Comp.PendingChassis.Value.Id, severity: 0.4f);
            ClearPending(machine);
            Dirty(machine);
            return;
        }

        if (!TryConsumeReagent(machine.Owner, machine.Comp.StabilizerReagent, machine.Comp.StabilizerNeeded))
        {
            _popup.PopupEntity(Loc.GetString("sol-bioterror-synth-need-stabilizer"), machine, user);
            return;
        }

        machine.Comp.CycleInProgress = true;
        machine.Comp.CycleEndsAt = _timing.CurTime + machine.Comp.SynthesisDelay;
        Dirty(machine);
        UpdateSynthesizerVisuals(machine);
        _popup.PopupEntity(Loc.GetString("sol-bioterror-synth-started"), machine, user);
        MarkMachineDeployed(machine.Owner, synthesizer: true);
    }

    private void TickIncubator(Entity<ClandestineCultureIncubatorComponent> machine)
    {
        if (machine.Comp.CycleInProgress)
        {
            if (!_power.IsPowered(machine.Owner))
            {
                // Power loss spoils the cycle.
                machine.Comp.CycleInProgress = false;
                RemComp<PendingCultureDataComponent>(machine.Owner);
                Dirty(machine);
                UpdateIncubatorVisuals(machine);
                TriggerAccident(machine.Owner, "SolPathogenFlu", severity: 0.25f);
                _popup.PopupEntity(Loc.GetString("sol-bioterror-incubator-spoiled"), machine, machine);
                return;
            }

            if (_timing.CurTime < machine.Comp.CycleEndsAt)
                return;

            machine.Comp.CycleInProgress = false;
            machine.Comp.HasFinishedCulture = true;
            machine.Comp.OvergrowAt = _timing.CurTime + TimeSpan.FromSeconds(45);
            Dirty(machine);
            UpdateIncubatorVisuals(machine);
            _popup.PopupEntity(Loc.GetString("sol-bioterror-incubator-complete"), machine, machine);
            return;
        }

        if (machine.Comp.HasFinishedCulture && _timing.CurTime >= machine.Comp.OvergrowAt)
        {
            machine.Comp.HasFinishedCulture = false;
            RemComp<PendingCultureDataComponent>(machine.Owner);
            Dirty(machine);
            UpdateIncubatorVisuals(machine);
            TriggerAccident(machine.Owner, "SolPathogenFlu", severity: 0.6f);
            _popup.PopupEntity(Loc.GetString("sol-bioterror-incubator-overgrown"), machine, machine);
        }
    }

    private void SpawnFinishedCulture(Entity<ClandestineCultureIncubatorComponent> machine, PendingCultureDataComponent pending)
    {
        var culture = Spawn("SolPathogenCultureVial", Transform(machine).Coordinates);
        var cultureComp = EnsureComp<PathogenCultureComponent>(culture);
        cultureComp.ChassisId = pending.ChassisId;
        cultureComp.Traits = new List<ProtoId<PathogenTraitPrototype>>(pending.Traits);
        cultureComp.IsChassisCulture = pending.Traits.Count == 0 || pending.ChassisId != null;
        cultureComp.Viability = Math.Clamp(pending.Quality * (pending.Contaminated ? 0.5f : 1f), 0.1f, 1f);
        cultureComp.SpoilsAt = _timing.CurTime + TimeSpan.FromMinutes(8);
        Dirty(culture, cultureComp);
    }

    private void TickSynthesizer(Entity<ClandestinePathogenSynthesizerComponent> machine)
    {
        if (!machine.Comp.CycleInProgress)
            return;

        if (!_power.IsPowered(machine.Owner))
        {
            machine.Comp.CycleInProgress = false;
            Dirty(machine);
            UpdateSynthesizerVisuals(machine);
            TriggerAccident(machine.Owner, machine.Comp.PendingChassis?.Id ?? "SolPathogenBioagent", severity: 0.5f);
            ClearPending(machine);
            _popup.PopupEntity(Loc.GetString("sol-bioterror-synth-spoiled"), machine, machine);
            return;
        }

        if (_timing.CurTime < machine.Comp.CycleEndsAt)
            return;

        machine.Comp.CycleInProgress = false;
        UpdateSynthesizerVisuals(machine);

        if (machine.Comp.PendingChassis == null)
        {
            Dirty(machine);
            return;
        }

        try
        {
            var def = _registry.RegisterStrain(
                machine.Comp.PendingChassis.Value,
                machine.Comp.PendingTraits,
                creator: null);

            var concentration = 4f * Math.Clamp(machine.Comp.PendingViability, 0.2f, 1f);
            for (var i = 0; i < machine.Comp.AmpoulesProduced; i++)
            {
                var ampoule = Spawn(machine.Comp.AmpoulePrototype, Transform(machine).Coordinates);
                var payload = EnsureComp<PathogenPayloadComponent>(ampoule);
                payload.StrainId = def.Id;
                payload.Concentration = concentration;
                payload.Kind = PathogenPayloadKind.Food;
                Dirty(ampoule, payload);
                _meta.SetEntityName(ampoule, Loc.GetString("sol-bioterror-ampoule-name", ("strain", def.DisplayName)));
            }

            // Also produce one aerosol canister.
            var aerosol = Spawn("SolPathogenAerosolCanister", Transform(machine).Coordinates);
            var aerosolPayload = EnsureComp<PathogenPayloadComponent>(aerosol);
            aerosolPayload.StrainId = def.Id;
            aerosolPayload.Concentration = concentration * 1.25f;
            aerosolPayload.Kind = PathogenPayloadKind.Aerosol;
            Dirty(aerosol, aerosolPayload);

            var synthEv = new BioterrorStrainSynthesizedEvent(def.Id, machine.Owner, null);
            RaiseLocalEvent(ref synthEv);
            UpdateTrackerSynthesized(def.Id);
            _popup.PopupEntity(Loc.GetString("sol-bioterror-synth-complete", ("strain", def.DisplayName)), machine, machine);
        }
        catch (InvalidOperationException)
        {
            TriggerAccident(machine.Owner, machine.Comp.PendingChassis.Value.Id, severity: 0.7f);
            _popup.PopupEntity(Loc.GetString("sol-bioterror-synth-failed"), machine, machine);
        }

        ClearPending(machine);
    }

    private bool TryConsumeReagent(EntityUid machine, string reagentId, float amount)
    {
        var needed = FixedPoint2.New(amount);
        var total = _solutions.GetTotalPrototypeQuantity(machine, reagentId);
        if (total < needed)
            return false;

        foreach (var (_, sol) in _solutions.EnumerateSolutions(machine))
        {
            var qty = sol.Comp.Solution.GetTotalPrototypeQuantity(reagentId);
            if (qty < needed)
                continue;

            _solutions.RemoveReagent(sol, reagentId, needed);
            return true;
        }

        return false;
    }

    private void TriggerAccident(EntityUid machine, string pathogenId, float severity)
    {
        _pathogen.AddOrIncreaseContamination(machine, pathogenId, 3f * severity);
        EntityManager.System<GridPathogenAtmosphereSystem>().AddAirborneLoad(machine, pathogenId, 4f * severity);

        var nearby = new HashSet<EntityUid>();
        // Infect unsealed operators standing on the same tile / nearby via exposure helper.
        var query = EntityQueryEnumerator<Content.Shared.Mobs.Components.MobStateComponent, TransformComponent>();
        while (query.MoveNext(out var mob, out _, out var xform))
        {
            if (xform.Coordinates.GetGridUid(EntityManager) != Transform(machine).GridUid)
                continue;
            if ((xform.Coordinates.Position - Transform(machine).Coordinates.Position).LengthSquared() > 2.25f)
                continue;

            nearby.Add(mob);
        }

        foreach (var mob in nearby)
        {
            _pathogen.TryExpose(mob, pathogenId, 1.5f * severity, PathogenTransmission.Airborne, machine);
        }
    }

    private void ClearPending(Entity<ClandestinePathogenSynthesizerComponent> machine)
    {
        machine.Comp.PendingChassis = null;
        machine.Comp.PendingTraits.Clear();
        machine.Comp.PendingViability = 1f;
        Dirty(machine);
    }

    private void MarkMachineDeployed(EntityUid machine, bool analyzer = false, bool incubator = false, bool synthesizer = false)
    {
        var tracker = EnsureTracker();
        if (analyzer)
            tracker.AnalyzerDeployed = true;
        if (incubator)
            tracker.IncubatorDeployed = true;
        if (synthesizer)
            tracker.SynthesizerDeployed = true;

        if (tracker.AnalyzerDeployed && tracker.IncubatorDeployed && tracker.SynthesizerDeployed)
        {
            var grid = Transform(machine).GridUid;
            tracker.LabEstablishedOffShuttle = tracker.SpawnShuttleGrid == null || grid != tracker.SpawnShuttleGrid;
        }
    }

    private void UpdateTrackerSynthesized(string strainId)
    {
        var tracker = EnsureTracker();
        tracker.SynthesizedStrainId = strainId;
    }

    private BioterrorCellTrackerComponent EnsureTracker()
    {
        var query = EntityQueryEnumerator<BioterrorCellTrackerComponent>();
        while (query.MoveNext(out _, out var tracker))
            return tracker;

        var holder = Spawn();
        return AddComp<BioterrorCellTrackerComponent>(holder);
    }

    private void UpdateAnalyzerVisuals(Entity<ClandestineSampleAnalyzerComponent> machine)
    {
        var state = !_power.IsPowered(machine.Owner)
            ? ClandestineLabVisualState.Off
            : machine.Comp.Processing
                ? ClandestineLabVisualState.Running
                : ClandestineLabVisualState.On;
        _appearance.SetData(machine.Owner, ClandestineLabVisuals.State, state);
    }

    private void UpdateIncubatorVisuals(Entity<ClandestineCultureIncubatorComponent> machine)
    {
        ClandestineLabVisualState state;
        if (machine.Comp.HasFinishedCulture)
            state = ClandestineLabVisualState.Open;
        else if (machine.Comp.CycleInProgress)
            state = ClandestineLabVisualState.Running;
        else if (_power.IsPowered(machine.Owner))
            state = ClandestineLabVisualState.On;
        else
            state = ClandestineLabVisualState.Off;

        _appearance.SetData(machine.Owner, ClandestineLabVisuals.State, state);
    }

    private void UpdateSynthesizerVisuals(Entity<ClandestinePathogenSynthesizerComponent> machine)
    {
        var state = !_power.IsPowered(machine.Owner)
            ? ClandestineLabVisualState.Off
            : machine.Comp.CycleInProgress
                ? ClandestineLabVisualState.Running
                : ClandestineLabVisualState.On;
        _appearance.SetData(machine.Owner, ClandestineLabVisuals.State, state);
    }

    private void OnAnalyzerExamined(Entity<ClandestineSampleAnalyzerComponent> ent, ref ExaminedEvent args)
    {
        if (ent.Comp.Processing)
            args.PushMarkup(Loc.GetString("sol-bioterror-analyzer-examine-running"));
        else
            args.PushMarkup(Loc.GetString("sol-bioterror-analyzer-examine"));
    }

    private void OnIncubatorExamined(Entity<ClandestineCultureIncubatorComponent> ent, ref ExaminedEvent args)
    {
        if (ent.Comp.CycleInProgress)
            args.PushMarkup(Loc.GetString("sol-bioterror-incubator-examine-running"));
        else if (ent.Comp.HasFinishedCulture)
            args.PushMarkup(Loc.GetString("sol-bioterror-incubator-examine-ready"));
        else
            args.PushMarkup(Loc.GetString("sol-bioterror-incubator-examine"));
    }

    private void OnSynthesizerExamined(Entity<ClandestinePathogenSynthesizerComponent> ent, ref ExaminedEvent args)
    {
        if (ent.Comp.CycleInProgress)
        {
            args.PushMarkup(Loc.GetString("sol-bioterror-synth-examine-running"));
            return;
        }

        var chassis = ent.Comp.PendingChassis?.Id ?? "none";
        var traits = ent.Comp.PendingTraits.Count == 0 ? "none" : string.Join(", ", ent.Comp.PendingTraits.Select(t => t.Id));
        args.PushMarkup(Loc.GetString("sol-bioterror-synth-examine", ("chassis", chassis), ("traits", traits)));
    }
}

/// <summary>
/// Temporary incubator state while a culture cycle runs or awaits retrieval.
/// </summary>
[RegisterComponent]
public sealed partial class PendingCultureDataComponent : Component
{
    [DataField]
    public ProtoId<PathogenPrototype>? ChassisId;

    [DataField]
    public List<ProtoId<PathogenTraitPrototype>> Traits = new();

    [DataField]
    public float Quality = 0.5f;

    [DataField]
    public bool Contaminated;
}
