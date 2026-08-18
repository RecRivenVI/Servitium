package servitium.fixture;

import java.io.BufferedReader;
import java.io.IOException;
import java.io.InputStreamReader;
import java.io.OutputStream;
import java.io.UncheckedIOException;
import java.nio.charset.Charset;
import java.nio.file.Path;
import java.util.ArrayList;
import java.util.List;
import java.util.Locale;

public final class Main {
    private Main() {
    }

    public static void main(String[] args) throws Exception {
        Config config = Config.parse(args);
        if (config.describe()) {
            System.out.printf(
                    Locale.ROOT,
                    "{\"startupDelayMs\":%d,\"outputIntervalMs\":%d,\"ignoreStop\":%s,\"crashExitCode\":%d,\"outputCharset\":\"%s\",\"explicitOutputCharset\":%s,\"splitOutput\":%s,\"probeOutput\":%s,\"spawnDepth\":%d}%n",
                    config.startupDelayMs(),
                    config.outputIntervalMs(),
                    config.ignoreStop(),
                    config.crashExitCode(),
                    config.outputCharset().name(),
                    config.explicitOutputCharset(),
                    config.splitOutput(),
                    config.probeOutput(),
                    config.spawnDepth());
            return;
        }

        if (config.startupDelayMs() > 0) {
            Thread.sleep(config.startupDelayMs());
        }

        writeLine(config, "fixture-pid:" + ProcessHandle.current().pid());
        writeLine(config, "fixture-ready");
        if (config.probeOutput()) {
            writeLine(config, "\u001b[32mfixture-unicode:\u4f3a\u670d\u5668\u7f16\u7801\u8fb9\u754c\u001b[0m");
        }

        if (config.crashExitCode() >= 0) {
            System.exit(config.crashExitCode());
        }

        if (config.spawnDepth() > 0) {
            spawnChild(config);
        }

        if (config.childMode()) {
            while (true) {
                Thread.sleep(1000);
            }
        }

        Thread outputThread = null;
        if (config.outputIntervalMs() > 0) {
            outputThread = Thread.ofPlatform().daemon(true).name("fixture-output").start(() -> {
                long sequence = 0;
                try {
                    while (!Thread.currentThread().isInterrupted()) {
                        writeLine(config, String.format(Locale.ROOT, "fixture-output-%d", sequence++));
                        Thread.sleep(config.outputIntervalMs());
                    }
                } catch (InterruptedException exception) {
                    Thread.currentThread().interrupt();
                } catch (IOException exception) {
                    throw new UncheckedIOException(exception);
                }
            });
        }

        readCommands(config);
        if (outputThread != null) {
            outputThread.interrupt();
        }
    }

    private static void readCommands(Config config) throws IOException {
        try (BufferedReader reader = new BufferedReader(
                new InputStreamReader(System.in, config.outputCharset()))) {
            String line;
            while ((line = reader.readLine()) != null) {
                if (line.equals("stop") && !config.ignoreStop()) {
                    writeLine(config, "fixture-stopping");
                    return;
                }

                writeLine(config, "fixture-input:" + line);
            }
        }
    }

    private static synchronized void writeLine(Config config, String value) throws IOException {
        if (!config.explicitOutputCharset() && !config.splitOutput()) {
            System.out.println(value);
            System.out.flush();
            return;
        }

        byte[] bytes = (value + System.lineSeparator()).getBytes(config.outputCharset());
        OutputStream output = System.out;
        if (config.splitOutput()) {
            for (byte valueByte : bytes) {
                output.write(valueByte);
                output.flush();
            }
        } else {
            output.write(bytes);
            output.flush();
        }
    }

    private static void spawnChild(Config config) throws Exception {
        String java = Path.of(System.getProperty("java.home"), "bin", "java.exe").toString();
        String jar = Path.of(Main.class.getProtectionDomain().getCodeSource().getLocation().toURI()).toString();
        List<String> command = new ArrayList<>();
        command.add(java);
        command.add("-jar");
        command.add(jar);
        command.add("--child-mode");
        command.add("--spawn-depth");
        command.add(Integer.toString(config.spawnDepth() - 1));
        new ProcessBuilder(command).inheritIO().start();
    }

    private record Config(
            long startupDelayMs,
            long outputIntervalMs,
            boolean ignoreStop,
            int crashExitCode,
            boolean describe,
            Charset outputCharset,
            boolean explicitOutputCharset,
            boolean splitOutput,
            boolean probeOutput,
            int spawnDepth,
            boolean childMode) {
        private static Config parse(String[] args) {
            long startupDelayMs = 0;
            long outputIntervalMs = 0;
            boolean ignoreStop = false;
            int crashExitCode = -1;
            boolean describe = false;
            Charset outputCharset = Charset.forName("UTF-8");
            boolean explicitOutputCharset = false;
            boolean splitOutput = false;
            boolean probeOutput = false;
            int spawnDepth = 0;
            boolean childMode = false;

            for (int index = 0; index < args.length; index++) {
                String argument = args[index];
                switch (argument) {
                    case "--startup-delay-ms" -> startupDelayMs = parseNonNegative(args, ++index, argument);
                    case "--output-interval-ms" -> outputIntervalMs = parseNonNegative(args, ++index, argument);
                    case "--ignore-stop" -> ignoreStop = true;
                    case "--crash-exit-code" -> crashExitCode = parseExitCode(args, ++index);
                    case "--describe" -> describe = true;
                    case "--output-charset" -> {
                        outputCharset = parseCharset(args, ++index, argument);
                        explicitOutputCharset = true;
                    }
                    case "--split-output" -> splitOutput = true;
                    case "--probe-output" -> probeOutput = true;
                    case "--spawn-depth" -> spawnDepth = (int) parseNonNegative(args, ++index, argument);
                    case "--child-mode" -> childMode = true;
                    default -> throw new IllegalArgumentException("Unknown argument: " + argument);
                }
            }

            if (spawnDepth > 8) {
                throw new IllegalArgumentException("--spawn-depth must not exceed 8");
            }

            return new Config(startupDelayMs, outputIntervalMs, ignoreStop, crashExitCode, describe, outputCharset, explicitOutputCharset, splitOutput, probeOutput, spawnDepth, childMode);
        }

        private static long parseNonNegative(String[] args, int index, String name) {
            if (index >= args.length) {
                throw new IllegalArgumentException("Missing value for " + name);
            }

            long value = Long.parseLong(args[index]);
            if (value < 0) {
                throw new IllegalArgumentException(name + " must be non-negative");
            }

            return value;
        }

        private static int parseExitCode(String[] args, int index) {
            long value = parseNonNegative(args, index, "--crash-exit-code");
            if (value > 255) {
                throw new IllegalArgumentException("--crash-exit-code must be between 0 and 255");
            }

            return (int) value;
        }

        private static Charset parseCharset(String[] args, int index, String name) {
            if (index >= args.length) {
                throw new IllegalArgumentException("Missing value for " + name);
            }

            Charset charset = Charset.forName(args[index]);
            if (!charset.name().equalsIgnoreCase("UTF-8") && !charset.name().equalsIgnoreCase("GBK")) {
                throw new IllegalArgumentException(name + " must be UTF-8 or GBK");
            }

            return charset;
        }
    }
}
