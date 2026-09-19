namespace JobTrack.Abstractions;

/// <summary>
///     Every <see cref="InvariantViolationException.ConstraintId" /> value a throw site can raise,
///     named once so a throw site and the catch site that switches on it (2026-09-18 remediation
///     plan 2.8) share a compile-time reference instead of two independently typed string literals,
///     where a typo in either one compiles silently. The architecture guard
///     <c>CodeStyle_ConstraintIdIsNamed</c> rejects a string literal as the first argument of
///     <see cref="InvariantViolationException" />'s constraint-id-carrying constructors anywhere in
///     <c>src</c> other than this file.
/// </summary>
public static class ConstraintIds
{
	/// <summary><c>ConstraintId</c> <c>"account-current-password-incorrect"</c>.</summary>
	public const string AccountCurrentPasswordIncorrect = "account-current-password-incorrect";

	/// <summary><c>ConstraintId</c> <c>"account-disabled"</c>.</summary>
	public const string AccountDisabled = "account-disabled";

	/// <summary><c>ConstraintId</c> <c>"account-locked-out"</c>.</summary>
	public const string AccountLockedOut = "account-locked-out";

	/// <summary><c>ConstraintId</c> <c>"account-new-password-policy"</c>.</summary>
	public const string AccountNewPasswordPolicy = "account-new-password-policy";

	/// <summary><c>ConstraintId</c> <c>"achievement-transition-not-permitted"</c>.</summary>
	public const string AchievementTransitionNotPermitted = "achievement-transition-not-permitted";

	/// <summary><c>ConstraintId</c> <c>"employee-username-already-taken"</c>.</summary>
	public const string EmployeeUsernameAlreadyTaken = "employee-username-already-taken";

	/// <summary><c>ConstraintId</c> <c>"hierarchy.missing-node"</c>.</summary>
	public const string HierarchyMissingNode = "hierarchy.missing-node";

	/// <summary><c>ConstraintId</c> <c>"hierarchy.node-has-both-children-and-leaf-work"</c>.</summary>
	public const string HierarchyNodeHasBothChildrenAndLeafWork = "hierarchy.node-has-both-children-and-leaf-work";

	/// <summary><c>ConstraintId</c> <c>"hierarchy.root-has-leaf-work"</c>.</summary>
	public const string HierarchyRootHasLeafWork = "hierarchy.root-has-leaf-work";

	/// <summary><c>ConstraintId</c> <c>"hierarchy.self-parent"</c>.</summary>
	public const string HierarchySelfParent = "hierarchy.self-parent";

	/// <summary><c>ConstraintId</c> <c>"home-node-must-not-be-leaf"</c>.</summary>
	public const string HomeNodeMustNotBeLeaf = "home-node-must-not-be-leaf";

	/// <summary><c>ConstraintId</c> <c>"home-node-target-not-active"</c>.</summary>
	public const string HomeNodeTargetNotActive = "home-node-target-not-active";

	/// <summary><c>ConstraintId</c> <c>"import-subtree-duplicate-local-id"</c>.</summary>
	public const string ImportSubtreeDuplicateLocalId = "import-subtree-duplicate-local-id";

	/// <summary><c>ConstraintId</c> <c>"import-subtree-empty"</c>.</summary>
	public const string ImportSubtreeEmpty = "import-subtree-empty";

	/// <summary><c>ConstraintId</c> <c>"import-subtree-invalid-work-achievement"</c>.</summary>
	public const string ImportSubtreeInvalidWorkAchievement = "import-subtree-invalid-work-achievement";

	/// <summary><c>ConstraintId</c> <c>"import-subtree-invalid-work-interval"</c>.</summary>
	public const string ImportSubtreeInvalidWorkInterval = "import-subtree-invalid-work-interval";

	/// <summary><c>ConstraintId</c> <c>"import-subtree-parent-cycle"</c>.</summary>
	public const string ImportSubtreeParentCycle = "import-subtree-parent-cycle";

	/// <summary><c>ConstraintId</c> <c>"import-subtree-unfinished-completed-work"</c>.</summary>
	public const string ImportSubtreeUnfinishedCompletedWork = "import-subtree-unfinished-completed-work";

	/// <summary><c>ConstraintId</c> <c>"import-subtree-unknown-parent-local-id"</c>.</summary>
	public const string ImportSubtreeUnknownParentLocalId = "import-subtree-unknown-parent-local-id";

	/// <summary><c>ConstraintId</c> <c>"import-subtree-unknown-prerequisite-local-id"</c>.</summary>
	public const string ImportSubtreeUnknownPrerequisiteLocalId = "import-subtree-unknown-prerequisite-local-id";

	/// <summary><c>ConstraintId</c> <c>"import-subtree-work-blocked-by-prerequisite"</c>.</summary>
	public const string ImportSubtreeWorkBlockedByPrerequisite = "import-subtree-work-blocked-by-prerequisite";

	/// <summary><c>ConstraintId</c> <c>"import-subtree-work-on-branch"</c>.</summary>
	public const string ImportSubtreeWorkOnBranch = "import-subtree-work-on-branch";

	/// <summary><c>ConstraintId</c> <c>"import-subtree-work-precedes-prerequisite"</c>.</summary>
	public const string ImportSubtreeWorkPrecedesPrerequisite = "import-subtree-work-precedes-prerequisite";

	/// <summary><c>ConstraintId</c> <c>"installation-already-initialised"</c>.</summary>
	public const string InstallationAlreadyInitialised = "installation-already-initialised";

	/// <summary><c>ConstraintId</c> <c>"job-node-already-claimed"</c>.</summary>
	public const string JobNodeAlreadyClaimed = "job-node-already-claimed";

	/// <summary><c>ConstraintId</c> <c>"job-node-decompose-requires-a-child"</c>.</summary>
	public const string JobNodeDecomposeRequiresAChild = "job-node-decompose-requires-a-child";

	/// <summary><c>ConstraintId</c> <c>"job-node-delete-worked-leaf-reason-required"</c>.</summary>
	public const string JobNodeDeleteWorkedLeafReasonRequired = "job-node-delete-worked-leaf-reason-required";

	/// <summary><c>ConstraintId</c> <c>"job-node-has-children-cannot-attach-leaf-work"</c>.</summary>
	public const string JobNodeHasChildrenCannotAttachLeafWork = "job-node-has-children-cannot-attach-leaf-work";

	/// <summary><c>ConstraintId</c> <c>"job-node-has-children-cannot-decompose"</c>.</summary>
	public const string JobNodeHasChildrenCannotDecompose = "job-node-has-children-cannot-decompose";

	/// <summary><c>ConstraintId</c> <c>"job-node-has-children-cannot-delete"</c>.</summary>
	public const string JobNodeHasChildrenCannotDelete = "job-node-has-children-cannot-delete";

	/// <summary><c>ConstraintId</c> <c>"job-node-has-prerequisites-cannot-delete"</c>.</summary>
	public const string JobNodeHasPrerequisitesCannotDelete = "job-node-has-prerequisites-cannot-delete";

	/// <summary><c>ConstraintId</c> <c>"job-node-holding-area-anchored"</c>.</summary>
	public const string JobNodeHoldingAreaAnchored = "job-node-holding-area-anchored";

	/// <summary><c>ConstraintId</c> <c>"job-node-is-root-cannot-attach-leaf-work"</c>.</summary>
	public const string JobNodeIsRootCannotAttachLeafWork = "job-node-is-root-cannot-attach-leaf-work";

	/// <summary><c>ConstraintId</c> <c>"job-node-is-root-cannot-decompose"</c>.</summary>
	public const string JobNodeIsRootCannotDecompose = "job-node-is-root-cannot-decompose";

	/// <summary><c>ConstraintId</c> <c>"job-node-is-root-cannot-delete"</c>.</summary>
	public const string JobNodeIsRootCannotDelete = "job-node-is-root-cannot-delete";

	/// <summary><c>ConstraintId</c> <c>"job-node-move-invalid"</c>.</summary>
	public const string JobNodeMoveInvalid = "job-node-move-invalid";

	/// <summary><c>ConstraintId</c> <c>"job-node-move-would-cycle"</c>.</summary>
	public const string JobNodeMoveWouldCycle = "job-node-move-would-cycle";

	/// <summary><c>ConstraintId</c> <c>"job-node-move-would-invalidate-prerequisite"</c>.</summary>
	public const string JobNodeMoveWouldInvalidatePrerequisite = "job-node-move-would-invalidate-prerequisite";

	/// <summary><c>ConstraintId</c> <c>"job-node-not-deletable"</c>.</summary>
	public const string JobNodeNotDeletable = "job-node-not-deletable";

	/// <summary><c>ConstraintId</c> <c>"job-node-root-owner-required"</c>.</summary>
	public const string JobNodeRootOwnerRequired = "job-node-root-owner-required";

	/// <summary><c>ConstraintId</c> <c>"job-node-write-rejected"</c>.</summary>
	public const string JobNodeWriteRejected = "job-node-write-rejected";

	/// <summary><c>ConstraintId</c> <c>"job-prerequisite-already-exists"</c>.</summary>
	public const string JobPrerequisiteAlreadyExists = "job-prerequisite-already-exists";

	/// <summary><c>ConstraintId</c> <c>"job-prerequisite-invalid"</c>.</summary>
	public const string JobPrerequisiteInvalid = "job-prerequisite-invalid";

	/// <summary><c>ConstraintId</c> <c>"job-prerequisite-is-hierarchy-edge"</c>.</summary>
	public const string JobPrerequisiteIsHierarchyEdge = "job-prerequisite-is-hierarchy-edge";

	/// <summary><c>ConstraintId</c> <c>"job-prerequisite-not-self"</c>.</summary>
	public const string JobPrerequisiteNotSelf = "job-prerequisite-not-self";

	/// <summary><c>ConstraintId</c> <c>"job-prerequisite-would-cycle"</c>.</summary>
	public const string JobPrerequisiteWouldCycle = "job-prerequisite-would-cycle";

	/// <summary><c>ConstraintId</c> <c>"leaf-closure-active-sessions"</c>.</summary>
	public const string LeafClosureActiveSessions = "leaf-closure-active-sessions";

	/// <summary><c>ConstraintId</c> <c>"leaf-work-already-attached"</c>.</summary>
	public const string LeafWorkAlreadyAttached = "leaf-work-already-attached";

	/// <summary><c>ConstraintId</c> <c>"node-rate-override-on-root"</c>.</summary>
	public const string NodeRateOverrideOnRoot = "node-rate-override-on-root";

	/// <summary><c>ConstraintId</c> <c>"node-rate-override-overlap"</c>.</summary>
	public const string NodeRateOverrideOverlap = "node-rate-override-overlap";

	/// <summary><c>ConstraintId</c> <c>"passkey-credential-material-policy"</c>.</summary>
	public const string PasskeyCredentialMaterialPolicy = "passkey-credential-material-policy";

	/// <summary><c>ConstraintId</c> <c>"passkey-max-count"</c>.</summary>
	public const string PasskeyMaxCount = "passkey-max-count";

	/// <summary><c>ConstraintId</c> <c>"passkey-name-duplicate"</c>.</summary>
	public const string PasskeyNameDuplicate = "passkey-name-duplicate";

	/// <summary><c>ConstraintId</c> <c>"passkey-name-policy"</c>.</summary>
	public const string PasskeyNamePolicy = "passkey-name-policy";

	/// <summary><c>ConstraintId</c> <c>"passkey-not-user-verified"</c>.</summary>
	public const string PasskeyNotUserVerified = "passkey-not-user-verified";

	/// <summary><c>ConstraintId</c> <c>"personal-access-token-expiry-not-in-future"</c>.</summary>
	public const string PersonalAccessTokenExpiryNotInFuture = "personal-access-token-expiry-not-in-future";

	/// <summary><c>ConstraintId</c> <c>"personal-access-token-expiry-too-long"</c>.</summary>
	public const string PersonalAccessTokenExpiryTooLong = "personal-access-token-expiry-too-long";

	/// <summary><c>ConstraintId</c> <c>"request-already-acknowledged"</c>.</summary>
	public const string RequestAlreadyAcknowledged = "request-already-acknowledged";

	/// <summary><c>ConstraintId</c> <c>"requester-job-required"</c>.</summary>
	public const string RequesterJobRequired = "requester-job-required";

	/// <summary><c>ConstraintId</c> <c>"requester-role-assigned-work"</c>.</summary>
	public const string RequesterRoleAssignedWork = "requester-role-assigned-work";

	/// <summary><c>ConstraintId</c> <c>"schedule-exception-already-exists"</c>.</summary>
	public const string ScheduleExceptionAlreadyExists = "schedule-exception-already-exists";

	/// <summary><c>ConstraintId</c> <c>"schedule-exception-priced-additive-overlap"</c>.</summary>
	public const string ScheduleExceptionPricedAdditiveOverlap = "schedule-exception-priced-additive-overlap";

	/// <summary><c>ConstraintId</c> <c>"schedule-version-overlap"</c>.</summary>
	public const string ScheduleVersionOverlap = "schedule-version-overlap";

	/// <summary><c>ConstraintId</c> <c>"subtree-delete-holding-area-anchored"</c>.</summary>
	public const string SubtreeDeleteHoldingAreaAnchored = "subtree-delete-holding-area-anchored";

	/// <summary><c>ConstraintId</c> <c>"subtree-delete-reason-required"</c>.</summary>
	public const string SubtreeDeleteReasonRequired = "subtree-delete-reason-required";

	/// <summary><c>ConstraintId</c> <c>"two-factor-key-missing"</c>.</summary>
	public const string TwoFactorKeyMissing = "two-factor-key-missing";

	/// <summary><c>ConstraintId</c> <c>"user-cost-rate-overlap"</c>.</summary>
	public const string UserCostRateOverlap = "user-cost-rate-overlap";

	/// <summary><c>ConstraintId</c> <c>"work-session-already-active"</c>.</summary>
	public const string WorkSessionAlreadyActive = "work-session-already-active";

	/// <summary><c>ConstraintId</c> <c>"work-session-finish-in-future"</c>.</summary>
	public const string WorkSessionFinishInFuture = "work-session-finish-in-future";

	/// <summary><c>ConstraintId</c> <c>"work-session-invalid-interval"</c>.</summary>
	public const string WorkSessionInvalidInterval = "work-session-invalid-interval";

	/// <summary><c>ConstraintId</c> <c>"work-session-leaf-closed"</c>.</summary>
	public const string WorkSessionLeafClosed = "work-session-leaf-closed";

	/// <summary><c>ConstraintId</c> <c>"work-session-overlap"</c>.</summary>
	public const string WorkSessionOverlap = "work-session-overlap";

	/// <summary><c>ConstraintId</c> <c>"work-session.same-user-leaf-overlap"</c>.</summary>
	public const string WorkSessionSameUserLeafOverlap = "work-session.same-user-leaf-overlap";

	/// <summary><c>ConstraintId</c> <c>"work-session-start-in-future"</c>.</summary>
	public const string WorkSessionStartInFuture = "work-session-start-in-future";

	/// <summary><c>ConstraintId</c> <c>"work-session-target-not-eligible"</c>.</summary>
	public const string WorkSessionTargetNotEligible = "work-session-target-not-eligible";
}
