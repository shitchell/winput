// RobotServer - persistent java.awt.Robot driver.
// Reads a line protocol on stdin, replays it as real Windows input events.
// One JVM per winput session; every keystroke is just a line down a pipe.
import java.awt.*;
import java.awt.datatransfer.*;
import java.awt.event.*;
import java.io.*;
import java.nio.charset.StandardCharsets;
import java.util.*;

public class RobotServer {
    static Robot robot;
    static final Map<String, Integer> KEYS = new HashMap<>();
    static final Map<Character, int[]> CHARS = new HashMap<>(); // char -> {keycode, needsShift}

    public static void main(String[] args) throws Exception {
        robot = new Robot();
        robot.setAutoDelay(Integer.getInteger("winput.delay", 8));
        buildKeys();
        buildChars();
        BufferedReader in = new BufferedReader(new InputStreamReader(System.in, StandardCharsets.UTF_8));
        PrintStream out = new PrintStream(new FileOutputStream(FileDescriptor.out), true, StandardCharsets.UTF_8);
        out.println("ready");
        String line;
        while ((line = in.readLine()) != null) {
            try {
                String res = handle(line);
                if (res != null) out.println(res);
            } catch (Exception e) {
                out.println("err " + e.getClass().getSimpleName() + ": " + e.getMessage());
            }
        }
    }

    static String handle(String line) throws Exception {
        if (line.isEmpty()) return null;
        char cmd = line.charAt(0);
        String rest = line.length() > 2 ? line.substring(2) : "";
        switch (cmd) {
            case 't': type(rest); return "ok";
            case 'k': for (String k : rest.trim().split("\\s+")) tap(key(k)); return "ok";
            case 'c': chord(rest.trim()); return "ok";
            case 'd': robot.keyPress(key(rest.trim())); return "ok";
            case 'u': robot.keyRelease(key(rest.trim())); return "ok";
            case 'm': {
                String[] p = rest.trim().split("\\s+");
                // No autoDelay for motion: position is idempotent, and the delay
                // caps throughput at ~87 moves/sec, which a fast mouse outruns.
                int keep = robot.getAutoDelay();
                robot.setAutoDelay(0);
                try { robot.mouseMove(Integer.parseInt(p[0]), Integer.parseInt(p[1])); }
                finally { robot.setAutoDelay(keep); }
                return "ok";
            }
            case 'b': return button(rest.trim());
            case 'w': robot.mouseWheel(Integer.parseInt(rest.trim())); return "ok";
            case 'p': paste(rest); return "ok";
            case 's': Thread.sleep(Long.parseLong(rest.trim())); return "ok";
            case 'q': System.exit(0); return null;
            case '?': {
                String r = query(line.substring(1).trim());
                return r.startsWith("err") ? r : r + "\nok";
            }
        }
        return "err unknown command '" + cmd + "'";
    }

    static String query(String what) {
        StringBuilder sb = new StringBuilder();
        if (what.equals("screens")) {
            GraphicsEnvironment ge = GraphicsEnvironment.getLocalGraphicsEnvironment();
            GraphicsDevice def = ge.getDefaultScreenDevice();
            GraphicsDevice[] devs = ge.getScreenDevices();
            for (int i = 0; i < devs.length; i++) {
                Rectangle b = devs[i].getDefaultConfiguration().getBounds();
                sb.append("= screen ").append(i).append(' ').append(devs[i].getIDstring())
                  .append(' ').append(b.x).append(' ').append(b.y)
                  .append(' ').append(b.width).append(' ').append(b.height)
                  .append(devs[i] == def ? " primary" : "").append('\n');
            }
            sb.setLength(Math.max(0, sb.length() - 1));
            return sb.toString();
        }
        if (what.equals("pointer")) {
            Point p = MouseInfo.getPointerInfo().getLocation();
            return "= pointer " + p.x + " " + p.y;
        }
        return "err unknown query '" + what + "'";
    }

    static void tap(int code) {
        robot.keyPress(code);
        robot.keyRelease(code);
    }

    static int key(String name) {
        Integer k = KEYS.get(name.toLowerCase());
        if (k != null) return k;
        if (name.length() == 1) {
            int[] m = CHARS.get(name.charAt(0));
            if (m != null) return m[0];
        }
        throw new IllegalArgumentException("no such key '" + name + "'");
    }

    static void chord(String spec) {
        String[] parts = spec.split("\\+");
        int[] mods = new int[parts.length - 1];
        for (int i = 0; i < parts.length - 1; i++) mods[i] = key(parts[i]);
        for (int m : mods) robot.keyPress(m);
        try {
            tap(key(parts[parts.length - 1]));
        } finally {
            for (int i = mods.length - 1; i >= 0; i--) robot.keyRelease(mods[i]);
        }
    }

    static String button(String spec) {
        String[] p = spec.split("\\s+");
        int n = Integer.parseInt(p[0]);
        String action = p.length > 1 ? p[1] : "click";
        int mask = n == 1 ? InputEvent.BUTTON1_DOWN_MASK
                 : n == 2 ? InputEvent.BUTTON2_DOWN_MASK
                 : n == 3 ? InputEvent.BUTTON3_DOWN_MASK
                 : 0;
        if (mask == 0) return "err bad button " + n;
        switch (action) {
            case "down": robot.mousePress(mask); break;
            case "up":   robot.mouseRelease(mask); break;
            case "dbl":  robot.mousePress(mask); robot.mouseRelease(mask);
                         robot.mousePress(mask); robot.mouseRelease(mask); break;
            default:     robot.mousePress(mask); robot.mouseRelease(mask);
        }
        return "ok";
    }

    // Types US-ASCII directly as key events. Anything else (unicode, emoji) is
    // batched and delivered via the clipboard, restoring the old contents after.
    static void type(String s) throws Exception {
        StringBuilder pending = new StringBuilder();
        for (int i = 0; i < s.length(); ) {
            int cp = s.codePointAt(i);
            i += Character.charCount(cp);
            int[] m = cp < 0x10000 ? CHARS.get((char) cp) : null;
            if (m == null) { pending.appendCodePoint(cp); continue; }
            if (pending.length() > 0) { paste(pending.toString()); pending.setLength(0); }
            if (m[1] == 1) {
                robot.keyPress(KeyEvent.VK_SHIFT);
                try { tap(m[0]); } finally { robot.keyRelease(KeyEvent.VK_SHIFT); }
            } else {
                tap(m[0]);
            }
        }
        if (pending.length() > 0) paste(pending.toString());
    }

    static void paste(String text) throws Exception {
        Clipboard cb = Toolkit.getDefaultToolkit().getSystemClipboard();
        String old = null;
        try { old = (String) cb.getData(DataFlavor.stringFlavor); } catch (Exception ignored) {}
        cb.setContents(new StringSelection(text), null);
        Thread.sleep(50);
        robot.keyPress(KeyEvent.VK_CONTROL);
        try { tap(KeyEvent.VK_V); } finally { robot.keyRelease(KeyEvent.VK_CONTROL); }
        Thread.sleep(90);
        if (old != null) cb.setContents(new StringSelection(old), null);
    }

    static void buildChars() {
        for (char c = 'a'; c <= 'z'; c++) CHARS.put(c, new int[]{KeyEvent.VK_A + (c - 'a'), 0});
        for (char c = 'A'; c <= 'Z'; c++) CHARS.put(c, new int[]{KeyEvent.VK_A + (c - 'A'), 1});
        for (char c = '0'; c <= '9'; c++) CHARS.put(c, new int[]{KeyEvent.VK_0 + (c - '0'), 0});
        String plain = "`-=[]\\;',./ ";
        int[] plainK = {KeyEvent.VK_BACK_QUOTE, KeyEvent.VK_MINUS, KeyEvent.VK_EQUALS,
                        KeyEvent.VK_OPEN_BRACKET, KeyEvent.VK_CLOSE_BRACKET, KeyEvent.VK_BACK_SLASH,
                        KeyEvent.VK_SEMICOLON, KeyEvent.VK_QUOTE, KeyEvent.VK_COMMA,
                        KeyEvent.VK_PERIOD, KeyEvent.VK_SLASH, KeyEvent.VK_SPACE};
        for (int i = 0; i < plain.length(); i++) CHARS.put(plain.charAt(i), new int[]{plainK[i], 0});
        String shifted = "~_+{}|:\"<>?";
        int[] shiftedK = {KeyEvent.VK_BACK_QUOTE, KeyEvent.VK_MINUS, KeyEvent.VK_EQUALS,
                          KeyEvent.VK_OPEN_BRACKET, KeyEvent.VK_CLOSE_BRACKET, KeyEvent.VK_BACK_SLASH,
                          KeyEvent.VK_SEMICOLON, KeyEvent.VK_QUOTE, KeyEvent.VK_COMMA,
                          KeyEvent.VK_PERIOD, KeyEvent.VK_SLASH};
        for (int i = 0; i < shifted.length(); i++) CHARS.put(shifted.charAt(i), new int[]{shiftedK[i], 1});
        String shiftNum = ")!@#$%^&*(";
        for (int i = 0; i < 10; i++) CHARS.put(shiftNum.charAt(i), new int[]{KeyEvent.VK_0 + i, 1});
        CHARS.put('\t', new int[]{KeyEvent.VK_TAB, 0});
        CHARS.put('\n', new int[]{KeyEvent.VK_ENTER, 0});
    }

    static void buildKeys() {
        put("enter return", KeyEvent.VK_ENTER);
        put("tab", KeyEvent.VK_TAB);
        put("esc escape", KeyEvent.VK_ESCAPE);
        put("bksp backspace", KeyEvent.VK_BACK_SPACE);
        put("del delete", KeyEvent.VK_DELETE);
        put("ins insert", KeyEvent.VK_INSERT);
        put("space", KeyEvent.VK_SPACE);
        put("home", KeyEvent.VK_HOME);
        put("end", KeyEvent.VK_END);
        put("pgup pageup", KeyEvent.VK_PAGE_UP);
        put("pgdn pagedown", KeyEvent.VK_PAGE_DOWN);
        put("up", KeyEvent.VK_UP);
        put("down", KeyEvent.VK_DOWN);
        put("left", KeyEvent.VK_LEFT);
        put("right", KeyEvent.VK_RIGHT);
        put("ctrl control", KeyEvent.VK_CONTROL);
        put("alt", KeyEvent.VK_ALT);
        put("shift", KeyEvent.VK_SHIFT);
        put("win meta super", KeyEvent.VK_WINDOWS);
        put("menu apps", KeyEvent.VK_CONTEXT_MENU);
        put("capslock", KeyEvent.VK_CAPS_LOCK);
        put("printscreen prtsc", KeyEvent.VK_PRINTSCREEN);
        put("pause", KeyEvent.VK_PAUSE);
        put("scrolllock", KeyEvent.VK_SCROLL_LOCK);
        put("numlock", KeyEvent.VK_NUM_LOCK);
        for (int i = 1; i <= 12; i++) put("f" + i, KeyEvent.VK_F1 + (i - 1));
        for (int i = 0; i <= 9; i++) put("num" + i, KeyEvent.VK_NUMPAD0 + i);
        put("numplus", KeyEvent.VK_ADD);
        put("numminus", KeyEvent.VK_SUBTRACT);
        put("numstar", KeyEvent.VK_MULTIPLY);
        put("numslash", KeyEvent.VK_DIVIDE);
        put("numdot", KeyEvent.VK_DECIMAL);
    }

    static void put(String names, int code) {
        for (String n : names.split(" ")) KEYS.put(n, code);
    }
}
