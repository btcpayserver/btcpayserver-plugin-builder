#!/usr/bin/env perl

use strict;
use warnings;
use bytes;
use IO::Select;
use POSIX qw(setsid WNOHANG);
use Time::HiRes qw(time sleep);

my ($max_bytes, $max_line_bytes, $max_lines, $timeout_seconds, $separator, @command) = @ARGV;
die "usage: limit-output.pl MAX_BYTES MAX_LINE_BYTES MAX_LINES TIMEOUT_SECONDS -- COMMAND...\n"
    unless defined $max_bytes && $max_bytes =~ /^\d+$/ && $max_bytes > 0
        && defined $max_line_bytes && $max_line_bytes =~ /^\d+$/ && $max_line_bytes > 0
        && defined $max_lines && $max_lines =~ /^\d+$/ && $max_lines > 0
        && defined $timeout_seconds && $timeout_seconds =~ /^\d+$/ && $timeout_seconds > 0
        && defined $separator && $separator eq '--' && @command;

pipe(my $reader, my $writer) or die "failed to create output pipe: $!\n";
my $child_pid = fork();
die "failed to fork build process: $!\n" unless defined $child_pid;

if ($child_pid == 0) {
    close $reader;
    setsid() or die "failed to create build process group: $!\n";
    open STDOUT, '>&', $writer or die "failed to redirect build stdout: $!\n";
    open STDERR, '>&', $writer or die "failed to redirect build stderr: $!\n";
    close $writer;
    exec { $command[0] } @command or die "failed to start build: $!\n";
}

close $writer;
my $selector = IO::Select->new($reader);
my $deadline = time() + $timeout_seconds;
my $total_bytes = 0;
my $line_bytes = 0;
my $lines = 0;

sub reap_children {
    while (waitpid(-1, WNOHANG) > 0) { }
}

sub terminate_build {
    my ($exit_code) = @_;
    kill 'TERM', -$child_pid;
    kill 'TERM', $child_pid;
    sleep 0.5;
    kill 'KILL', -$child_pid;
    kill 'KILL', $child_pid;
    reap_children();
    exit $exit_code;
}

$SIG{TERM} = sub { terminate_build(143) };
$SIG{INT} = sub { terminate_build(130) };
$SIG{HUP} = sub { terminate_build(129) };

$| = 1;
my $pipe_open = 1;
my $child_status;
while ($pipe_open || !defined $child_status) {
    if (!defined $child_status) {
        my $waited = waitpid($child_pid, WNOHANG);
        $child_status = $? if $waited == $child_pid;
    }

    my $remaining = $deadline - time();
    terminate_build(124) if $remaining <= 0;

    if (!$pipe_open) {
        sleep($remaining > 0.1 ? 0.1 : $remaining);
        next;
    }

    my @ready = $selector->can_read($remaining > 0.1 ? 0.1 : $remaining);
    next unless @ready;

    my $read = sysread($reader, my $chunk, 8192);
    die "failed to read build output: $!\n" unless defined $read;
    if ($read == 0) {
        $pipe_open = 0;
        $selector->remove($reader);
        close $reader;
        next;
    }

    while (length $chunk) {
        my $newline = index($chunk, "\n");
        my $segment_length = $newline < 0 ? length($chunk) : $newline + 1;

        terminate_build(78) if $line_bytes == 0 && $lines == $max_lines;
        $lines++ if $line_bytes == 0;

        my $remaining_total = $max_bytes - $total_bytes;
        my $remaining_line = $max_line_bytes - $line_bytes;
        my $allowed = $segment_length;
        $allowed = $remaining_total if $remaining_total < $allowed;
        $allowed = $remaining_line if $remaining_line < $allowed;

        if ($allowed > 0) {
            my $output = substr($chunk, 0, $allowed, '');
            print STDOUT $output or terminate_build(1);
            $total_bytes += $allowed;
            $line_bytes += $allowed;
        }

        terminate_build(78) if $allowed < $segment_length;
        $line_bytes = 0 if $newline >= 0;
    }
}

my $status = $child_status;
reap_children();

exit 1 if $status == -1;
exit 128 + ($status & 127) if $status & 127;
exit($status >> 8);
