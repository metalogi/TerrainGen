#!/usr/bin/perl
# PreToolUse guard for the PR workflow (see CLAUDE.md > Contribution Workflow).
#
# Denies three things, so an agent that forgets the convention cannot bypass it:
#   1. `gh pr merge`             - merging is the reviewer's call, never an agent's
#   2. any push aimed at trunk   - `git push origin main`, `... HEAD:main`, etc.
#   3. commit or push while HEAD is on trunk
#
# Everything else passes through untouched. Always exits 0; a denial is expressed
# as JSON on stdout, not as a non-zero exit.
#
# Perl, not node: the node on PATH here is v0.12 and cannot parse modern syntax.
# JSON::PP has been core Perl since 5.14 and Git Bash ships 5.26.

use strict;
use warnings;
use JSON::PP;

my $raw = do { local $/; <STDIN> };
exit 0 unless defined $raw && $raw =~ /\S/;

# Set when the text being scanned is not a clean single command, so the
# separator anchoring in invokes() cannot be trusted and every pattern is
# matched anywhere in the line instead. Costs some false positives; the
# alternative is a guard that silently stops guarding.
my $unanchored = 0;

my $cmd;
my $parsed = eval { decode_json($raw) };
if ($parsed && ref $parsed eq 'HASH' && ref $parsed->{tool_input} eq 'HASH') {
    $cmd = $parsed->{tool_input}{command};
}

# If the payload did not parse, scan the raw text rather than waving the call
# through. In raw JSON the command is preceded by a quote rather than a shell
# separator, so anchored matching would find nothing at all - hence unanchored.
unless (defined $cmd && $cmd =~ /\S/) {
    $cmd        = $raw;
    $unanchored = 1;
}

# A newline separates commands exactly like `;` does. Collapsing it to a space
# (as this once did) hid every line after the first from the guard.
(my $line = $cmd) =~ s/\r?\n/ ; /g;
$line =~ s/\s+/ /g;
$line =~ s/^\s+//;   # a single leading space would otherwise defeat /^/

# `bash -c "..."` puts the real command inside quotes, where the anchoring
# below would never see it. Anything carrying a -c shell wrapper is scanned
# unanchored.
$unanchored = 1
  if $line =~ /(?:^|[;&|(]\s*)(?:ba|da|z|k)?sh\s+(?:-\S+\s+)*-c\b/
  || $line =~ /(?:^|[;&|(]\s*)(?:pwsh|powershell)(?:\.exe)?\s+.*-(?:c|Command)\b/i;

sub deny {
    my ($reason) = @_;
    print JSON::PP->new->canonical->encode({
        hookSpecificOutput => {
            hookEventName            => 'PreToolUse',
            permissionDecision       => 'deny',
            permissionDecisionReason => $reason,
        },
    });
    exit 0;
}

# True when the pattern starts the command or follows a shell separator, so a
# quoted mention such as `echo "git push origin main"` does not trip the guard.
# When $unanchored is set that concession is withdrawn - see above.
sub invokes {
    my ($pattern) = @_;
    return $line =~ /$pattern/ if $unanchored;
    return $line =~ /(?:^|[;&|(]\s*)$pattern/;
}

# `git` accepts global options before the subcommand, so `git -C . push` and
# `git --no-pager push` must be recognised as pushes too.
my $GIT = qr/git(?:\s+-\S+(?:\s+\S+)?)*/;

if (invokes(qr/gh\s+pr\s+merge\b/)) {
    deny(
        "Blocked by .claude/hooks/guard-main.pl. Merging a PR is the reviewer's "
      . "decision, not an agent's. Leave the PR open and tell the user it is ready to merge."
    );
}

# `/` is in the ref-prefix class so `refs/heads/main` is caught alongside
# `origin main` and `HEAD:main`. The trailing lookahead rather than `(?:\s|$)`
# so a closing quote still ends the ref, while `main-fix` stays untouched.
if (invokes(qr/$GIT\s+push\b[^;&|]*[\s:+\/](?:main|master)(?![-\w.\/])/)) {
    deny(
        "Blocked by .claude/hooks/guard-main.pl. This pushes directly to the trunk. "
      . "Push the feature branch instead and open a PR with /ship."
    );
}

# --all and --mirror name no ref but push trunk along with everything else.
if (invokes(qr/$GIT\s+push\b[^;&|]*\s--(?:all|mirror)\b/)) {
    deny(
        "Blocked by .claude/hooks/guard-main.pl. This pushes every local branch, "
      . "trunk included. Push the one feature branch by name instead."
    );
}

my $root = $ENV{CLAUDE_PROJECT_DIR};
chdir $root if defined $root && -d $root;

my $branch = `git rev-parse --abbrev-ref HEAD 2>/dev/null`;
$branch = '' unless defined $branch;
chomp $branch;

if ($branch =~ /^(?:main|master)$/ && invokes(qr/$GIT\s+(?:commit|push)\b/)) {
    deny(
        "Blocked by .claude/hooks/guard-main.pl. HEAD is on $branch. Create a feature "
      . "branch first (git checkout -b <name>), then commit. See CLAUDE.md > Contribution Workflow."
    );
}

exit 0;
