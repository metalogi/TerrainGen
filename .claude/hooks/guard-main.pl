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

my $cmd;
my $parsed = eval { decode_json($raw) };
if ($parsed && ref $parsed eq 'HASH' && ref $parsed->{tool_input} eq 'HASH') {
    $cmd = $parsed->{tool_input}{command};
}

# If the payload did not parse, scan the raw text rather than waving the call
# through. Coarser, but a broken parser must not silently disable the guard.
$cmd = $raw unless defined $cmd && $cmd =~ /\S/;

(my $line = $cmd) =~ s/\s+/ /g;

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
sub invokes {
    my ($pattern) = @_;
    return $line =~ /(?:^|[;&|(]\s*)$pattern/;
}

if (invokes(qr/gh\s+pr\s+merge\b/)) {
    deny(
        "Blocked by .claude/hooks/guard-main.pl. Merging a PR is the reviewer's "
      . "decision, not an agent's. Leave the PR open and tell the user it is ready to merge."
    );
}

if (invokes(qr/git\s+push\b[^;&|]*[\s:+](?:main|master)(?:\s|$)/)) {
    deny(
        "Blocked by .claude/hooks/guard-main.pl. This pushes directly to the trunk. "
      . "Push the feature branch instead and open a PR with /ship."
    );
}

my $root = $ENV{CLAUDE_PROJECT_DIR};
chdir $root if defined $root && -d $root;

my $branch = `git rev-parse --abbrev-ref HEAD 2>/dev/null`;
$branch = '' unless defined $branch;
chomp $branch;

if ($branch =~ /^(?:main|master)$/ && invokes(qr/git\s+(?:commit|push)\b/)) {
    deny(
        "Blocked by .claude/hooks/guard-main.pl. HEAD is on $branch. Create a feature "
      . "branch first (git checkout -b <name>), then commit. See CLAUDE.md > Contribution Workflow."
    );
}

exit 0;
